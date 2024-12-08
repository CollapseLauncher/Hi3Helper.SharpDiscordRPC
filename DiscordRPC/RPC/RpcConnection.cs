using DiscordRPC.Events;
using DiscordRPC.Helper;
using DiscordRPC.IO;
using DiscordRPC.Logging;
using DiscordRPC.Message;
using DiscordRPC.RPC.Commands;
using DiscordRPC.RPC.Payload;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace DiscordRPC.RPC
{
    /// <summary>
    /// Communicates between the client and discord through RPC
    /// </summary>
    internal class RpcConnection : IDisposable
    {
        /// <summary>
        /// Version of the RPC Protocol
        /// </summary>
        public static readonly int VERSION = 1;

        /// <summary>
        /// The rate of poll to the discord pipe.
        /// </summary>
        public static readonly int POLL_RATE = 1000;

        /// <summary>
        /// Should we send a null presence on the farewells?
        /// </summary>
        private static readonly bool CLEAR_ON_SHUTDOWN = true;

        /// <summary>
        /// Should we work in a lock step manner? This option is semi-obsolete and may not work as expected.
        /// </summary>
        private static readonly bool LOCK_STEP = false;

        /// <summary>
        /// The logger used by the RPC connection
        /// </summary>
        public ILoggerRpc ILoggerRpc
        {
            get { return _iLoggerRpc; }
            set
            {
                _iLoggerRpc = value;
                if (namedPipe != null)
                    namedPipe.ILoggerRpc = value;
            }
        }
        private ILoggerRpc _iLoggerRpc;

        /// <summary>
        /// Called when a message is received from the RPC and is about to be enqueued. This is cross-thread and will execute on the RPC thread.
        /// </summary>
        public event OnRpcMessageEvent OnRpcMessage;

        #region States

        /// <summary>
        /// The current state of the RPC connection
        /// </summary>
        public RpcState State
        {
            get
            {
                lock (l_states)
                    return _state;
            }
        }
        private RpcState _state;
        private readonly object l_states = new object();

        /// <summary>
        /// The configuration received by the Ready
        /// </summary>
        public Configuration Configuration { get { Configuration tmp; lock (l_config) tmp = _configuration; return tmp; } }
        private          Configuration _configuration;
        private readonly object        l_config = new();

        private volatile bool aborting;
        private volatile bool shutdown;

        /// <summary>
        /// Indicates if the RPC connection is still running in the background
        /// </summary>
        public bool IsRunning { get { return thread != null; } }

        /// <summary>
        /// Forces the <see cref="Close"/> to call <see cref="Shutdown"/> instead, safely saying goodbye to Discord.
        /// <para>This option helps prevents ghosting in applications where the Process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence and we are closing the connection manually, instead of waiting for the process to terminate.</para>
        /// </summary>
        public bool ShutdownOnly { get; set; }

        #endregion

        #region Privates

        private string applicationID;                    // ID of the Discord APP
        private int processID;                            // ID of the process to track

        private long nonce;                                // Current command index

        private Thread thread;                            // The current thread
        private INamedPipeClient namedPipe;

        private int targetPipe;                                // The pipe to target. Leave as -1 for any available pipe.

        private readonly object l_rtqueue = new object();    // Lock for the send queue
        private readonly uint _maxRtQueueSize;
        private Queue<ICommand> _rtqueue;                    // The send queue

        private readonly object l_rxqueue = new object();    // Lock for the receive queue
        private readonly uint _maxRxQueueSize;              // The max size of the RX queue
        private Queue<IMessage> _rxqueue;                   // The receive queue

        private AutoResetEvent queueUpdatedEvent = new AutoResetEvent(false);
        private BackoffDelay delay;                     // The backoff delay before reconnecting.
        #endregion

        /// <summary>
        /// Creates a new instance of the RPC.
        /// </summary>
        /// <param name="applicationID">The ID of the Discord App</param>
        /// <param name="processID">The ID of the currently running process</param>
        /// <param name="targetPipe">The target pipe to connect too</param>
        /// <param name="client">The pipe client we shall use.</param>
        /// <param name="maxRxQueueSize">The maximum size of the out queue</param>
        /// <param name="maxRtQueueSize">The maximum size of the in queue</param>
        /// <param name="logger">Logger interface</param>
    #nullable enable
        public RpcConnection(string           applicationID,
                             int              processID,
                             int              targetPipe,
                             INamedPipeClient client,
                             uint             maxRxQueueSize = 128,
                             uint             maxRtQueueSize = 512,
                             ILoggerRpc?         logger = null)
        {
            this.applicationID = applicationID;
            this.processID = processID;
            this.targetPipe = targetPipe;
            this.namedPipe = client;
            this.ShutdownOnly = true;

            // Assign a default logger
            ILoggerRpc = logger ?? new ConsoleILoggerRpc();

            delay = new BackoffDelay(500, 60 * 1000);
            _maxRtQueueSize = maxRtQueueSize;
            _rtqueue = new Queue<ICommand>((int)_maxRtQueueSize + 1);

            _maxRxQueueSize = maxRxQueueSize;
            _rxqueue = new Queue<IMessage>((int)_maxRxQueueSize + 1);

            nonce = 0;
        }

        private long GetNextNonce()
        {
            nonce += 1;
            return nonce;
        }

        #region Queues
        /// <summary>
        /// Enqueues a command
        /// </summary>
        /// <param name="command">The command to enqueue</param>
        internal void EnqueueCommand(ICommand command)
        {
            ILoggerRpc.Trace("Enqueue Command: {0}", command.GetType().FullName);

            // We cannot add anything else if we are aborting or shutting down.
            if (aborting || shutdown) return;

            // Enqueue the set presence argument
            lock (l_rtqueue)
            {
                // If we are too big drop the last element
                if (_rtqueue.Count == _maxRtQueueSize)
                {
                    ILoggerRpc.Error("Too many enqueued commands, dropping oldest one. Maybe you are pushing new presences to fast?");
                    _rtqueue.Dequeue();
                }

                // Enqueue the message
                _rtqueue.Enqueue(command);
            }
        }

        /// <summary>
        /// Adds a message to the message queue. Does not copy the message, so be sure to copy it yourself or dereference it.
        /// </summary>
        /// <param name="message">The message to add</param>
        private void EnqueueMessage(IMessage message)
        {
            // Invoke the message
            try
            {
                if (OnRpcMessage != null)
                    OnRpcMessage.Invoke(this, message);
            }
            catch (Exception e)
            {
                ILoggerRpc.Error("Unhandled Exception while processing event: {0}", e.GetType().FullName);
                ILoggerRpc.Error(e.Message);
                ILoggerRpc.Error(e.StackTrace);
            }

            // Small queue sizes should just ignore messages
            if (_maxRxQueueSize <= 0)
            {
                ILoggerRpc.Trace("Enqueued Message, but queue size is 0.");
                return;
            }

            // Large queue sizes should keep the queue in check
            ILoggerRpc.Trace("Enqueue Message: {0}", message.Type);
            lock (l_rxqueue)
            {
                // If we are too big drop the last element
                if (_rxqueue.Count == _maxRxQueueSize)
                {
                    ILoggerRpc.Warning("Too many enqueued messages, dropping oldest one.");
                    _rxqueue.Dequeue();
                }

                // Enqueue the message
                _rxqueue.Enqueue(message);
            }
        }

        /// <summary>
        /// Dequeues a single message from the event stack. Returns null if none are available.
        /// </summary>
        /// <returns></returns>
        internal IMessage? DequeueMessage()
        {
            // Logger.Trace("Deque Message");
            lock (l_rxqueue)
            {
                // We have nothing, so just return null.
                if (_rxqueue.Count == 0) return null;

                // Get the value and remove it from the list at the same time
                return _rxqueue.Dequeue();
            }
        }

        /// <summary>
        /// Dequeues all messages from the event stack.
        /// </summary>
        /// <returns></returns>
        internal IMessage[] DequeueMessages()
        {
            // Logger.Trace("Deque Multiple Messages");
            lock (l_rxqueue)
            {
                // Copy the messages into an array
                IMessage[] messages = _rxqueue.ToArray();

                // Clear the entire queue
                _rxqueue.Clear();

                // return the array
                return messages;
            }
        }
        #endregion

        /// <summary>
        /// Main thread loop
        /// </summary>
        private void MainLoop()
        {
            // initialize the pipe
            ILoggerRpc.Info("RPC Connection Started");
            if (ILoggerRpc.Level <= LogLevel.Trace)
            {
                ILoggerRpc.Trace("============================");
                ILoggerRpc.Trace("Assembly:             " + Assembly.GetAssembly(typeof(RichPresence))?.FullName);
                ILoggerRpc.Trace("Pipe:                 " + namedPipe.GetType().FullName);
                ILoggerRpc.Trace("Platform:             " + Environment.OSVersion);
                ILoggerRpc.Trace("applicationID:        " + applicationID);
                ILoggerRpc.Trace("targetPipe:           " + targetPipe);
                ILoggerRpc.Trace("POLL_RATE:            " + POLL_RATE);
                ILoggerRpc.Trace("_maxRtQueueSize:      " + _maxRtQueueSize);
                ILoggerRpc.Trace("_maxRxQueueSize:      " + _maxRxQueueSize);
                ILoggerRpc.Trace("============================");
            }

            // Forever trying to connect unless the abort signal is sent
            // Keep Alive Loop
            while (!aborting && !shutdown)
            {
                try
                {
                    // Wrap everything up in a try get
                    // Dispose of the pipe if we have any (could be broken)
                    if (namedPipe == null)
                    {
                        ILoggerRpc.Error("Something bad has happened with our pipe client!");
                        aborting = true;
                        return;
                    }

                    // Connect to a new pipe
                    ILoggerRpc.Trace("Connecting to the pipe through the {0}", namedPipe.GetType().FullName);
                    if (namedPipe.Connect(targetPipe))
                    {
                        #region Connected
                        // We connected to a pipe! Reset the delay
                        ILoggerRpc.Trace("Connected to the pipe. Attempting to establish handshake...");
                        EnqueueMessage(new ConnectionEstablishedMessage() { ConnectedPipe = namedPipe.ConnectedPipe });

                        // Attempt to establish a handshake
                        EstablishHandshake();
                        ILoggerRpc.Trace("Connection Established. Starting reading loop...");

                        // Continuously iterate, waiting for the frame
                        // We want to only stop reading if the inside tells us (mainloop), if we are aborting (abort) or the pipe disconnects
                        // We dont want to exit on a shutdown, as we still have information
                        PipeFrame frame;
                        bool mainloop = true;
                        while (mainloop && !aborting && !shutdown && namedPipe.IsConnected)
                        {
                            #region Read Loop

                            // Iterate over every frame we have queued up, processing its contents
                            if (namedPipe.ReadFrame(out frame))
                            {
                                #region Read Payload
                                ILoggerRpc.Trace("Read Payload: {0}", frame.Opcode);

                                // Do some basic processing on the frame
                                switch (frame.Opcode)
                                {
                                    // We have been told by discord to close, so we will consider it an abort
                                    case Opcode.Close:

                                        ClosePayload close = frame.GetObject<ClosePayload>();
                                        ILoggerRpc.Warning("We have been told to terminate by discord: ({0}) {1}", close.Code, close.Reason);
                                        EnqueueMessage(new CloseMessage() { Code = close.Code, Reason = close.Reason });
                                        mainloop = false;
                                        break;

                                    // We have pinged, so we will flip it and respond back with pong
                                    case Opcode.Ping:
                                        ILoggerRpc.Trace("PING");
                                        frame.Opcode = Opcode.Pong;
                                        namedPipe.WriteFrame(frame);
                                        break;

                                    // We have ponged? I have no idea if Discord actually sends ping/pongs.
                                    case Opcode.Pong:
                                        ILoggerRpc.Trace("PONG");
                                        break;

                                    // A frame has been sent, we should deal with that
                                    case Opcode.Frame:
                                        if (shutdown)
                                        {
                                            // We are shutting down, so skip it
                                            ILoggerRpc.Warning("Skipping frame because we are shutting down.");
                                            break;
                                        }

                                        if (frame.Data == null)
                                        {
                                            // We have invalid data, that's not good.
                                            ILoggerRpc.Error("We received no data from the frame so we cannot get the event payload!");
                                            break;
                                        }

                                        // We have a frame, so we are going to process the payload and add it to the stack
                                        EventPayload? response = null;
                                        try { response = frame.GetObject<EventPayload>(); } catch (Exception e)
                                        {
                                            ILoggerRpc.Error("Failed to parse event! {0}", e.Message);
                                            ILoggerRpc.Error("Data: {0}", frame.Message);
                                        }

                                        try { if (response != null) ProcessFrame(response); } catch(Exception e)
                                        {
                                            ILoggerRpc.Error("Failed to process event! {0}", e.Message);
                                            ILoggerRpc.Error("Data: {0}", frame.Message);
                                        }

                                        break;

                                    default:
                                    case Opcode.Handshake:
                                        // We have a invalid opcode, better terminate to be safe
                                        ILoggerRpc.Error("Invalid opcode: {0}", frame.Opcode);
                                        mainloop = false;
                                        break;
                                }

                                #endregion
                            }

                            if (!aborting && namedPipe.IsConnected)
                            {
                                // Process the entire command queue we have left
                                ProcessCommandQueue();

                                // Wait for some time, or until a command has been queued up
                                queueUpdatedEvent.WaitOne(POLL_RATE);
                            }

                            #endregion
                        }
                        #endregion

                        ILoggerRpc.Trace("Left main read loop for some reason. Aborting: {0}, Shutting Down: {1}", aborting, shutdown);
                    }
                    else
                    {
                        ILoggerRpc.Error("Failed to connect for some reason.");
                        EnqueueMessage(new ConnectionFailedMessage() { FailedPipe = targetPipe });
                    }

                    // If we are not aborting, we have to wait a bit before trying to connect again
                    if (!aborting && !shutdown)
                    {
                        // We have disconnected for some reason, either a failed pipe or a bad reading,
                        // so we are going to wait a bit before doing it again
                        long sleep = delay.NextDelay();

                        ILoggerRpc.Trace("Waiting {0}ms before attempting to connect again", sleep);
                        Thread.Sleep(delay.NextDelay());
                    }
                }
                // catch(InvalidPipeException e)
                // {
                //    Logger.Error("Invalid Pipe Exception: {0}", e.Message);
                // }
                catch (Exception e)
                {
                    ILoggerRpc.Error("Unhandled Exception: {0}", e.GetType().FullName);
                    ILoggerRpc.Error(e.Message);
                    ILoggerRpc.Error(e.StackTrace);
                }
                finally
                {
                    // Disconnect from the pipe because something bad has happened. An exception has been thrown or the main read loop has terminated.
                    if (namedPipe is { IsConnected: true })
                    {
                        // Terminate the pipe
                        ILoggerRpc.Trace("Closing the named pipe.");
                        namedPipe.Close();
                    }

                    // Update our state
                    SetConnectionState(RpcState.Disconnected);
                }
            }

            // We have disconnected, so dispose of the thread and the pipe.
            ILoggerRpc.Trace("Left Main Loop");
            if (namedPipe != null)
                namedPipe.Dispose();

            ILoggerRpc.Info("Thread Terminated, no longer performing RPC connection.");
        }

        #region Reading

        /// <summary>Handles the response from the pipe and calls appropriate events and changes states.</summary>
        /// <param name="response">The response received by the server.</param>
        private void ProcessFrame(EventPayload response)
        {
            ILoggerRpc.Info("Handling Response. Cmd: {0}, Event: {1}", response.Command, response.Event);

            // Check if it is an error
            if (response.Event.HasValue && response.Event.Value == ServerEvent.ERROR)
            {
                // We have an error
                ILoggerRpc.Error("Error received from the RPC");

                // Create the event object and push it to the queue
                ErrorMessage err = response.GetObject<ErrorMessage>();
                ILoggerRpc.Error("Server responded with an error message: ({0}) {1}", err.Code.ToString(), err.Message);

                // Enqueue the message and then end
                EnqueueMessage(err);
                return;
            }

            // Check if its a handshake
            if (State == RpcState.Connecting)
            {
                if (response.Command == Command.DISPATCH && response.Event.HasValue && response.Event.Value == ServerEvent.READY)
                {
                    ILoggerRpc.Info("Connection established with the RPC");
                    SetConnectionState(RpcState.Connected);
                    delay.Reset();

                    // Prepare the object
                    ReadyMessage ready = response.GetObject<ReadyMessage>();
                    lock (l_config)
                    {
                        _configuration = ready.Configuration;
                        ready.User.SetConfiguration(_configuration);
                    }

                    // Enqueue the message
                    EnqueueMessage(ready);
                    return;
                }
            }

            if (State == RpcState.Connected)
            {
                switch(response.Command)
                {
                    // We were sent a dispatch, better process it
                    case Command.DISPATCH:
                        ProcessDispatch(response);
                        break;

                    // We were sent a Activity Update, better enqueue it
                    case Command.SET_ACTIVITY:
                        if (response.Data == null)
                        {
                            EnqueueMessage(new PresenceMessage());
                        }
                        else
                        {
                            RichPresenceResponse rp = response.GetObject<RichPresenceResponse>();
                            EnqueueMessage(new PresenceMessage(rp));
                        }
                        break;

                    case Command.UNSUBSCRIBE:
                    case Command.SUBSCRIBE:

                        // Go through the data, looking for the evt property, casting it to a server event
                        var serverEvent = response.GetObject<EventPayload>().Event;
                        if (serverEvent != null)
                        {
                            var evt = serverEvent.Value;

                            // Enqueue the appropriate message.
                            if (response.Command == Command.SUBSCRIBE)
                                EnqueueMessage(new SubscribeMessage(evt));
                            else
                                EnqueueMessage(new UnsubscribeMessage(evt));
                        }

                        break;

                    case Command.SEND_ACTIVITY_JOIN_INVITE:
                        ILoggerRpc.Trace("Got invite response ack.");
                        break;

                    case Command.CLOSE_ACTIVITY_JOIN_REQUEST:
                        ILoggerRpc.Trace("Got invite response reject ack.");
                        break;

                    // we have no idea what we were sent
                    default:
                        ILoggerRpc.Error("Unknown frame was received! {0}", response.Command);
                        return;
                }
                return;
            }

            ILoggerRpc.Trace("Received a frame while we are disconnected. Ignoring. Cmd: {0}, Event: {1}", response.Command, response.Event);
        }

        private void ProcessDispatch(EventPayload response)
        {
            if (response.Command != Command.DISPATCH) return;
            if (!response.Event.HasValue) return;

            switch(response.Event.Value)
            {
                // We are to join the server
                case ServerEvent.ACTIVITY_SPECTATE:
                    var spectate = response.GetObject<SpectateMessage>();
                    EnqueueMessage(spectate);
                    break;

                case ServerEvent.ACTIVITY_JOIN:
                    var join = response.GetObject<JoinMessage>();
                    EnqueueMessage(join);
                    break;

                case ServerEvent.ACTIVITY_JOIN_REQUEST:
                    var request = response.GetObject<JoinRequestMessage>();
                    EnqueueMessage(request);
                    break;

                // Unknown dispatch event received. We should just ignore it.
                default:
                    ILoggerRpc.Warning("Ignoring {0}", response.Event.Value);
                    break;
            }
        }

        #endregion

        #region Writting

        private void ProcessCommandQueue()
        {
            // Logger.Info("Checking command queue");

            // We are not ready yet, dont even try
            if (State != RpcState.Connected)
                return;

            // We are aborting, so we will just log a warning so we know this is probably only going to send the CLOSE
            if (aborting)
                ILoggerRpc.Warning("We have been told to write a queue but we have also been aborted.");

            // Prepare some variables we will clone into with locks
            bool      needsWriting = true;
            ICommand? item;

            // Continue looping until we dont need anymore messages
            while (needsWriting && namedPipe.IsConnected)
            {
                lock (l_rtqueue)
                {
                    // Pull the value and update our writing needs
                    // If we have nothing to write, exit the loop
                    needsWriting = _rtqueue.Count > 0;
                    if (!needsWriting) break;

                    // Peek at the item
                    item = _rtqueue.Peek();
                }

                // Break out of the loop as soon as we send this item
                if (shutdown || (!aborting && LOCK_STEP))
                    needsWriting = false;

                // Prepare the payload
                IPayload payload = item.PreparePayload(GetNextNonce());
                ILoggerRpc.Trace("Attempting to send payload: {0}", payload.Command);

                // Prepare the frame
                PipeFrame frame = new PipeFrame();
                if (item is CloseCommand)
                {
                    // We have been sent a close frame. We better just send a handwave
                    // Send it off to the server
                    SendHandwave();

                    // Queue the item
                    ILoggerRpc.Trace("Handwave sent, ending queue processing.");
                    lock (l_rtqueue) _rtqueue.Dequeue();

                    // Stop sending any more messages
                    return;
                }
                else
                {
                    if (aborting)
                    {
                        // We are aborting, so just dequeue the message and dont bother sending it
                        ILoggerRpc.Warning("- skipping frame because of abort.");
                        lock (l_rtqueue) _rtqueue.Dequeue();
                    }
                    else
                    {
                        // Prepare the frame
                        frame.SetObject(Opcode.Frame, payload);

                        // Write it and if it wrote perfectly fine, we will dequeue it
                        ILoggerRpc.Trace("Sending payload: {0}", payload.Command);
                        if (namedPipe.WriteFrame(frame))
                        {
                            // We sent it, so now dequeue it
                            ILoggerRpc.Trace("Sent Successfully.");
                            lock (l_rtqueue) _rtqueue.Dequeue();
                        }
                        else
                        {
                            // Something went wrong, so just give up and wait for the next time around.
                            ILoggerRpc.Warning("Something went wrong during writing!");
                            return;
                        }
                    }
                }
            }
        }

        #endregion

        #region Connection

        /// <summary>
        /// Establishes the handshake with the server.
        /// </summary>
        /// <returns></returns>
        private void EstablishHandshake()
        {
            ILoggerRpc.Trace("Attempting to establish a handshake...");

            // We are establishing a lock and not releasing it until we sent the handshake message.
            // We need to set the key, and it would not be nice if someone did things between us setting the key.

            // Check its state
            if (State != RpcState.Disconnected)
            {
                ILoggerRpc.Error("State must be disconnected in order to start a handshake!");
                return;
            }

            // Send it off to the server
            ILoggerRpc.Trace("Sending Handshake...");
            if (!namedPipe.WriteFrame(new PipeFrame(Opcode.Handshake, new Handshake() { Version = VERSION, ClientID = applicationID })))
            {
                ILoggerRpc.Error("Failed to write a handshake.");
                return;
            }

            // This has to be done outside the lock
            SetConnectionState(RpcState.Connecting);
        }

        /// <summary>
        /// Establishes a farewell with the server by sending a handwave.
        /// </summary>
        private void SendHandwave()
        {
            ILoggerRpc.Info("Attempting to wave goodbye...");

            // Check its state
            if (State == RpcState.Disconnected)
            {
                ILoggerRpc.Error("State must NOT be disconnected in order to send a handwave!");
                return;
            }

            // Send the handwave
            if (!namedPipe.WriteFrame(new PipeFrame(Opcode.Close, new Handshake() { Version = VERSION, ClientID = applicationID })))
            {
                ILoggerRpc.Error("failed to write a handwave.");
            }
        }

        /// <summary>
        /// Attempts to connect to the pipe. Returns true on success
        /// </summary>
        /// <returns></returns>
        public bool AttemptConnection()
        {
            ILoggerRpc.Info("Attempting a new connection");

            // The thread mustn't exist already
            if (thread != null)
            {
                ILoggerRpc.Error("Cannot attempt a new connection as the previous connection thread is not null!");
                return false;
            }

            // We have to be in the disconnected state
            if (State != RpcState.Disconnected)
            {
                ILoggerRpc.Warning("Cannot attempt a new connection as the previous connection hasn't changed state yet.");
                return false;
            }

            if (aborting)
            {
                ILoggerRpc.Error("Cannot attempt a new connection while aborting!");
                return false;
            }

            // Start the thread up
            thread = new Thread(MainLoop);
            thread.Name = "Discord IPC Thread";
            thread.IsBackground = true;
            thread.Start();

            return true;
        }

        /// <summary>
        /// Sets the current state of the pipe, locking the l_states object for thread safety.
        /// </summary>
        /// <param name="state">The state to set it too.</param>
        private void SetConnectionState(RpcState state)
        {
            ILoggerRpc.Trace("Setting the connection state to {0}", state.ToString().ToSnakeCase().ToUpperInvariant());
            lock (l_states)
            {
                _state = state;
            }
        }

        /// <summary>
        /// Closes the connection and disposes of resources. This will not force termination, but instead allow Discord disconnect us after we say goodbye.
        /// <para>This option helps prevents ghosting in applications where the Process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence and we are closing the connection manually, instead of waiting for the process to terminate.</para>
        /// </summary>
        public void Shutdown()
        {
            // Enable the flag
            ILoggerRpc.Trace("Initiated shutdown procedure");
            shutdown = true;

            // Clear the commands and enqueue the close
            lock(l_rtqueue)
            {
                _rtqueue.Clear();
                if (CLEAR_ON_SHUTDOWN) _rtqueue.Enqueue(new PresenceCommand() { PID = processID, Presence = null });
                _rtqueue.Enqueue(new CloseCommand());
            }

            // Trigger the event
            queueUpdatedEvent.Set();
        }

        /// <summary>
        /// Closes the connection and disposes of resources.
        /// </summary>
        public void Close()
        {
            if (thread == null)
            {
                ILoggerRpc.Error("Cannot close as it is not available!");
                return;
            }

            if (aborting)
            {
                ILoggerRpc.Error("Cannot abort as it has already been aborted");
                return;
            }

            // Set the abort state
            if (ShutdownOnly)
            {
                Shutdown();
                return;
            }

            // Terminate
            ILoggerRpc.Trace("Updating Abort State...");
            aborting = true;
            queueUpdatedEvent.Set();
        }

        /// <summary>
        /// Closes the connection and disposes resources. Identical to <see cref="Close"/> but ignores the "ShutdownOnly" value.
        /// </summary>
        public void Dispose()
        {
            ShutdownOnly = false;
            Close();
        }
        #endregion

    }

    /// <summary>
    /// State of the RPC connection
    /// </summary>
    internal enum RpcState
    {
        /// <summary>
        /// Disconnected from the discord client
        /// </summary>
        Disconnected,

        /// <summary>
        /// Connecting to the discord client. The handshake has been sent and we are awaiting the ready event
        /// </summary>
        Connecting,

        /// <summary>
        /// We are connect to the client and can send and receive messages.
        /// </summary>
        Connected
    }
}
