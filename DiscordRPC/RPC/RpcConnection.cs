using DiscordRPC.Entities;
using DiscordRPC.Helper;
using DiscordRPC.IO;
using DiscordRPC.Message;
using DiscordRPC.RPC.Commands;
using DiscordRPC.RPC.Payload;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;
// ReSharper disable UnusedMember.Global
#pragma warning disable CA1873

namespace DiscordRPC.RPC;

/// <summary>
/// Communicates between the client and discord through RPC
/// </summary>
internal class RpcConnection : IDisposable
{
    /// <summary>
    /// Version of the RPC Protocol
    /// </summary>
    public static readonly int Version = 1;

    /// <summary>
    /// The rate of poll to the discord pipe.
    /// </summary>
    public const int PollRate = 1000;

    /// <summary>
    /// Should we send a null presence on the farewells?
    /// </summary>
    private const bool ClearOnShutdown = true;

    /// <summary>
    /// Should we work in a lock step manner? This option is semi-obsolete and may not work as expected.
    /// </summary>
    private const bool LockStep = false;

    /// <summary>
    /// The logger used by the RPC connection
    /// </summary>
    public ILogger? Logger
    {
        get;
        set
        {
            field = value;
            _namedPipe?.Logger = value;
        }
    }

    private Timer? _timer;

    /// <summary>
    /// Called when a message is received from the RPC and is about to be enqueued. This is cross-thread and will execute on the RPC thread.
    /// </summary>
    public event OnRpcMessageEvent? OnRpcMessage;

    #region States

    /// <summary>
    /// The current state of the RPC connection
    /// </summary>
    public RpcState State
    {
        get
        {
            using (_lStates.EnterScope())
                return _state;
        }
    }
    private RpcState _state;
    private readonly Lock _lStates = new();
    private readonly Lock _lConfig = new();

    /// <summary>
    /// The configuration received by the Ready
    /// </summary>
    public Configuration? Configuration
    {
        get;
        set;
    }

    private volatile bool _aborting;
    private volatile bool _shutdown;

    /// <summary>
    /// Indicates if the RPC connection is still running in the background
    /// </summary>
    public bool IsRunning => _thread != null;

    /// <summary>
    /// Forces the <see cref="Close"/> to call <see cref="Shutdown"/> instead, safely saying goodbye to Discord. 
    /// <para>This option helps prevents ghosting in applications where the process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence, and we are closing the connection manually, instead of waiting for the process to terminate.</para>
    /// </summary>
    public bool ShutdownOnly { get; set; }

    #endregion

    #region Privates

    private readonly string _applicationID; // ID of the Discord APP
    private readonly int    _processID;     // ID of the process to track

    private long _nonce; // Current command index

    private          Thread?                 _thread; // The current thread
    private readonly ManagedNamedPipeClient? _namedPipe;

    private readonly int _targetPipe; // The pipe to taget. Leave as -1 for any available pipe.

    private readonly uint _maxRtQueueSize;
    private readonly uint _maxRxQueueSize;

    private readonly ConcurrentQueue<ICommand>    _rtQueue; // The send queue
    private readonly ConcurrentQueue<MessageBase> _rxQueue; // The receive queue

    private readonly AutoResetEvent _queueUpdatedEvent = new(false);
    private readonly BackoffDelay   _delay; // The backoff delay before reconnecting.
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
    /// <param name="logger">The logger for the instance</param>
    public RpcConnection(string applicationID, int processID, int targetPipe, ManagedNamedPipeClient client, ILogger? logger = null, uint maxRxQueueSize = 128, uint maxRtQueueSize = 512)
    {
        _applicationID = applicationID;
        _processID     = processID;
        _targetPipe    = targetPipe;
        _namedPipe     = client;
        ShutdownOnly   = true;

        // Assign a default logger
        Logger = logger;

        _delay          = new BackoffDelay(500, 60 * 1000);
        _maxRtQueueSize = maxRtQueueSize;
        _maxRxQueueSize = maxRxQueueSize;
        _rtQueue        = new ConcurrentQueue<ICommand>();
        _rxQueue        = new ConcurrentQueue<MessageBase>();

        _nonce = 0;
    }

    private long GetNextNonce()
    {
        _nonce += 1;
        return _nonce;
    }

    #region Queues

    /// <summary>
    /// Enqueues a command
    /// </summary>
    /// <param name="command">The command to enqueue</param>
    internal void EnqueueCommand(ICommand command)
    {
        Logger?.LogTrace("Enqueue Command: {}", command.GetType().FullName);

        // We cannot add anything else if we are aborting or shutting down.
        if (_aborting || _shutdown) return;

        // Enqueue the set presence argument
        // If we are too big drop the last element
        if (_rtQueue.Count == _maxRtQueueSize)
        {
            Logger?.LogError("Too many enqueued commands, dropping oldest one. Maybe you are pushing new presences to fast?");
            _rtQueue.TryDequeue(out _);
        }

        // Enqueue the message
        _rtQueue.Enqueue(command);
    }

    /// <summary>
    /// Adds a message to the message queue. Does not copy the message, so be sure to copy it yourself or dereference it.
    /// </summary>
    /// <param name="message">The message to add</param>
    private void EnqueueMessage(MessageBase? message)
    {
        // Invoke the message
        try
        {
            if (message != null)
            {
                OnRpcMessage?.Invoke(this, message);
            }
        }
        catch (Exception e)
        {
            Logger?.LogError("Unhandled Exception while processing event: {}", e.GetType().FullName);
            Logger?.LogError(e, "{EMessage}\r\n{EStackTrace}", e.Message, e.StackTrace);
        }

        // Small queue sizes should just ignore messages
        if (_maxRxQueueSize <= 0)
        {
            Logger?.LogTrace("Enqueued Message, but queue size is 0.");
            return;
        }

        // Large queue sizes should keep the queue in check
        Logger?.LogTrace("Enqueue Message: {}", message?.Type);

        // If we are too big drop the last element
        if (_rxQueue.Count == _maxRxQueueSize)
        {
            Logger?.LogWarning("Too many enqueued messages, dropping oldest one.");
            _rxQueue.TryDequeue(out _);
        }

        // Enqueue the message
        if (message != null)
        {
            _rxQueue.Enqueue(message);
        }
    }

    /// <summary>
    /// Dequeues a single message from the event stack. Returns null if none are available.
    /// </summary>
    /// <returns></returns>
    internal MessageBase? DequeueMessage()
    {
        // Logger?.LogTrace("Deque Message");
        // We have nothing, so just return null.
        if (_rxQueue.IsEmpty) return null;

        // Get the value and remove it from the list at the same time
        _rxQueue.TryDequeue(out MessageBase? message);
        return message;
    }

    /// <summary>
    /// Dequeues all messages from the event stack. 
    /// </summary>
    /// <returns></returns>
    internal MessageBase[] DequeueMessages()
    {
        // Logger?.LogTrace("Deque Multiple Messages");
        // Copy the messages into an array
        MessageBase[] messages = [.. _rxQueue];

        // Clear the entire queue
        _rxQueue.Clear();

        // return the array
        return messages;
    }

    #endregion

    /// <summary>
    /// Main thread loop
    /// </summary>
    private void MainLoop()
    {
        if (_timer != null)
        {
            return;
        }

        // initialize the pipe
        Logger?.LogInformation("RPC Connection Started");
        if (Logger?.IsEnabled(LogLevel.Trace) ?? false)
        {
            Logger.LogTrace("============================");
            Logger.LogTrace("Assembly:             {}", Assembly.GetAssembly(typeof(RichPresence))?.FullName);
            Logger.LogTrace("Pipe:                 {}", _namedPipe?.GetType().FullName);
            Logger.LogTrace("Platform:             {}", Environment.OSVersion);
            Logger.LogTrace("DotNet:               {}", Environment.Version);
            Logger.LogTrace("applicationID:        {}", _applicationID);
            Logger.LogTrace("targetPipe:           {}", _targetPipe);
            Logger.LogTrace("POLL_RATE:            {}", PollRate);
            Logger.LogTrace("_maxRtQueueSize:      {}", _maxRtQueueSize);
            Logger.LogTrace("_maxRxQueueSize:      {}", _maxRxQueueSize);
            Logger.LogTrace("============================");
        }

        _timer = new Timer(PollRate);
        _timer.Elapsed += WaitHandleEvent;
        _timer.Start();
    }

    private async void WaitHandleEvent(object? sender, ElapsedEventArgs args)
    {
        try
        {
            // Exit if timer has already been disposed (null)
            if (_timer == null)
            {
                return;
            }

            _timer.Stop(); // Pause the timer while we process

            if (_namedPipe == null)
            {
                Logger?.LogError("Something bad has happened with our pipe client!");
                _aborting = true;
                return;
            }

            // If aborting and shutdown signal is set, dispose the namedPipe and exit.
            if (_aborting || _shutdown)
            {
                // Dispose timer to avoid double calls to UpdateEvent
                if (Interlocked.Exchange(ref _timer, null) is { } oldTimer)
                {
                    oldTimer.Stop();
                    oldTimer.Elapsed -= WaitHandleEvent;
                    oldTimer.Dispose();
                }

                // We have disconnected, so dispose of the thread and the pipe.
                Logger?.LogTrace("Left Main Loop");
                _namedPipe?.Dispose();

                Logger?.LogInformation("Thread Terminated, no longer performing RPC connection.");
                return;
            }

            if (!_namedPipe.IsConnected)
            {
                // Update our state
                SetConnectionState(RpcState.Disconnected);

                // Connect to a new pipe
                Logger?.LogTrace("Connecting to the pipe through the {}", _namedPipe.GetType().FullName);
                if (await _namedPipe.TryConnectAsync(_targetPipe))
                {
                    #region Connected
                    // We connected to a pipe! Reset the delay
                    Logger?.LogTrace("Connected to the pipe. Attempting to establish handshake...");

                    EnqueueMessage(new ConnectionEstablishedMessage());

                    // Attempt to establish a handshake
                    EstablishHandshake();
                    Logger?.LogTrace("Connection Established. Starting reading RPC frames with {PollRate}ms interval...", PollRate);
                    #endregion
                }
                else
                {
                    Logger?.LogError("Failed to connect for some reason.");
                    EnqueueMessage(new ConnectionFailedMessage { FailedPipe = _targetPipe });
                }
            }

            // Continuously iterate, waiting for the frame
            // Iterate over every frame we have queued up, processing its contents
            bool mainLoop = true;
            while (mainLoop && _namedPipe.IsConnected)
            {
                if (_namedPipe.ReadFrame(out PipeFrame? frame) && frame != null)
                {
                    #region Read Frame
                    Logger?.LogTrace("Read Payload: {}", frame.Opcode);

                    // Do some basic processing on the frame
                    switch (frame.Opcode)
                    {
                        // We have been told by discord to close, so we will consider it an abort
                        case Opcode.Close:

                            ClosePayload? close = frame.GetObject(JsonSerializationContext.Default.ClosePayload);
                            Logger?.LogWarning("We have been told to terminate by discord: ({code}) {reason}", close?.Code ?? 0, close?.Reason);
                            EnqueueMessage(new CloseMessage { Code = close?.Code ?? 0, Reason = close?.Reason });
                            mainLoop = false;
                            break;

                        // We have pinged, so we will flip it and respond back with pong
                        case Opcode.Ping:
                            Logger?.LogTrace("PING");
                            frame.Opcode = Opcode.Pong;
                            _namedPipe.WriteFrame(frame);
                            break;

                        // We have ponged? I have no idea if Discord actually sends ping/pongs.
                        case Opcode.Pong:
                            Logger?.LogTrace("PONG");
                            break;

                        // A frame has been sent, we should deal with that
                        case Opcode.Frame:
                            if (_shutdown)
                            {
                                //We are shutting down, so skip it
                                Logger?.LogWarning("Skipping frame because we are shutting down.");
                                break;
                            }

                            if (frame.Data.Length == 0)
                            {
                                // We have invalid data, that's not good.
                                Logger?.LogError("We received no data from the frame so we cannot get the event payload!");
                                break;
                            }

                            // We have a frame, so we are going to process the payload and add it to the stack
                            EventPayload? response = null;
                            try { response = frame.GetObject(JsonSerializationContext.Default.EventPayload); }
                            catch (Exception e)
                            {
                                Logger?.LogError("Failed to parse event! {}", e.Message);
                                Logger?.LogError("Data: {}", frame.Message);
                            }


                            try { if (response != null) ProcessFrame(response); }
                            catch (Exception e)
                            {
                                Logger?.LogError(e, "Failed to process event! {}", e.Message);
                                Logger?.LogError("Data: {}", frame.Message);
                            }

                            break;


                        default:
                        case Opcode.Handshake:
                            // We have an invalid opcode, better terminate to be safe
                            Logger?.LogError("Invalid opcode: {}", frame.Opcode);
                            mainLoop = false;
                            break;
                    }

                    #endregion
                }

                if (_aborting || !_namedPipe.IsConnected)
                {
                    continue;
                }

                // Process the entire command queue we have left
                ProcessCommandQueue();

                // Wait for some time, or until a command has been queued up
                _queueUpdatedEvent.WaitOne(PollRate);

                break;
            }

            // If we are not aborting, we have to wait a bit before trying to connect again
            if (_aborting || _shutdown || _namedPipe.IsConnected)
            {
                return;
            }

            // We have disconnected for some reason, either a failed pipe or a bad reading,
            // so we are going to wait a bit before doing it again
            long sleep = _delay.NextDelay();

            Logger?.LogTrace("Waiting {}ms before attempting to connect again", sleep);
            Thread.Sleep((int)sleep);
        }
        catch
        {
            //Disconnect from the pipe because something bad has happened. An exception has been thrown or the main read loop has terminated.
            if (_namedPipe?.IsConnected ?? false)
            {
                //Terminate the pipe
                Logger?.LogTrace("Closing the named pipe.");
                _namedPipe.Close();
            }
        }
        finally
        {
            _timer?.Start(); // Resume the timer.
        }
    }

    #region Reading

    /// <summary>Handles the response from the pipe and calls appropriate events and changes states.</summary>
    /// <param name="response">The response received by the server.</param>
    private void ProcessFrame(EventPayload response)
    {
        Logger?.LogInformation("Handling Response. Cmd: {command}, Event: {event}", response.Command, response.Event);

        // Check if it is an error
        if (response.Event is ServerEvent.Error)
        {
            // We have an error
            Logger?.LogError("Error received from the RPC");

            // Create the event object and push it to the queue
            ErrorMessage? err = response.GetObject(JsonSerializationContext.Default.ErrorMessage);
            Logger?.LogError("Server responded with an error message: ({code}) {msg}", err?.Code.ToString(), err?.Message);

            // Enqueue the message and then end
            EnqueueMessage(err);
            return;
        }

        // Check if it's a handshake
        if (State == RpcState.Connecting)
        {
            if (response is { Command: Command.Dispatch, Event: ServerEvent.Ready })
            {
                Logger?.LogInformation("Connection established with the RPC");
                SetConnectionState(RpcState.Connected);
                _delay.Reset();

                // Prepare the object
                ReadyMessage? ready = response.GetObject(JsonSerializationContext.Default.ReadyMessage);
                lock (_lConfig)
                {
                    Configuration = ready?.Configuration;
                    ready?.User?.SetConfiguration(Configuration);
                }

                // Enqueue the message
                EnqueueMessage(ready);
                return;
            }
        }

        if (State == RpcState.Connected)
        {
            switch (response.Command)
            {
                // We were sent a dispatch, better process it
                case Command.Dispatch:
                    ProcessDispatch(response);
                    break;

                // We were sent an Activity Update, better enqueue it
                case Command.SetActivity:
                    if (response.Data == null)
                    {
                        EnqueueMessage(new PresenceMessage());
                    }
                    else
                    {
                        RichPresenceResponse? rp = response.GetObject(JsonSerializationContext.Default.RichPresenceResponse);
                        EnqueueMessage(new PresenceMessage(rp));
                    }
                    break;

                case Command.Unsubscribe:
                case Command.Subscribe:

                    // Prepare a serializer that can account for snake_case enums.
                    // JsonSerializer serializer = new JsonSerializer();
                    // serializer.Converters.Add(new Converters.EnumSnakeCaseConverter());


                    // Go through the data, looking for the evt property, casting it to a server event
                    ServerEvent evt = response.GetObject(JsonSerializationContext.Default.EventPayload)?.Event ?? default;

                    // Enqueue the appropriate message.
                    if (response.Command == Command.Subscribe)
                        EnqueueMessage(new SubscribeMessage(evt));
                    else
                        EnqueueMessage(new UnsubscribeMessage(evt));

                    break;


                case Command.SendActivityJoinInvite:
                    Logger?.LogTrace("Got invite response ack.");
                    break;

                case Command.CloseActivityJoinRequest:
                    Logger?.LogTrace("Got invite response reject ack.");
                    break;

                //we have no idea what we were sent
                default:
                    Logger?.LogError("Unknown frame was received! {}", response.Command);
                    return;
            }
            return;
        }

        Logger?.LogTrace("Received a frame while we are disconnected. Ignoring. Cmd: {cmd}, Event: {event}", response.Command, response.Event);
    }

    private void ProcessDispatch(EventPayload response)
    {
        if (response.Command != Command.Dispatch) return;
        if (!response.Event.HasValue) return;

        switch (response.Event.Value)
        {
            // We are to join the server
            case ServerEvent.ActivitySpectate:
                SpectateMessage? spectate = response.GetObject(JsonSerializationContext.Default.SpectateMessage);
                EnqueueMessage(spectate);
                break;

            case ServerEvent.ActivityJoin:
                JoinMessage? join = response.GetObject(JsonSerializationContext.Default.JoinMessage);
                EnqueueMessage(join);
                break;

            case ServerEvent.ActivityJoinRequest:
                JoinRequestMessage? request = response.GetObject(JsonSerializationContext.Default.JoinRequestMessage);
                EnqueueMessage(request);
                break;

            // Unknown dispatch event received. We should just ignore it.
            default:
                Logger?.LogWarning("Ignoring {}", response.Event.Value);
                break;
        }
    }

    #endregion

    #region Writting

    private void ProcessCommandQueue()
    {
        // Logger?.LogInformation("Checking command queue");

        // We are not ready yet, don't even try
        if (State != RpcState.Connected)
            return;

        // We are aborting, so we will just log a warning so we know this is probably only going to send the CLOSE
        if (_aborting)
            Logger?.LogWarning("We have been told to write a queue but we have also been aborted.");

        if (_namedPipe == null)
            return;

        // Prepare some variables we will clone into with locks
        bool needsWriting = true;

        // Continue looping until we don't need anymore messages
        while (needsWriting && _namedPipe.IsConnected)
        {
            // Pull the value and update our writing needs
            // If we have nothing to write, exit the loop
            needsWriting = !_rtQueue.IsEmpty;
            if (!needsWriting) break;

            //Peek at the item
            if (!_rtQueue.TryPeek(out ICommand? item))
            {
                break;
            }

            //Break out of the loop as soon as we send this item
            if (_shutdown || (!_aborting && LockStep))
                needsWriting = false;

            //Prepare the payload
            PayloadBase payload = item.PreparePayload(GetNextNonce());
            Logger?.LogTrace("Attempting to send payload: {}", payload.Command);

            //Prepare the frame
            PipeFrame frame = PipeFrame.Create();
            if (item is CloseCommand)
            {
                //We have been sent a close frame. We better just send handwave
                //Send it off to the server
                SendHandwave();

                //Queue the item
                Logger?.LogTrace("Handwave sent, ending queue processing.");
                _rtQueue.TryDequeue(out _);

                //Stop sending any more messages
                return;
            }

            if (_aborting)
            {
                //We are aborting, so just dequeue the message and don't bother sending it
                Logger?.LogWarning("- skipping frame because of abort.");
                _rtQueue.TryDequeue(out _);
            }
            else
            {
                //Prepare the frame
                Type          payloadType    = payload.GetType();
                JsonTypeInfo? typeOfTypeInfo = JsonSerializationContext.Default.GetTypeInfo(payloadType);
                if (typeOfTypeInfo == null)
                {
                    string exMsg = $"Payload Type: {payloadType.Name} is not supported!";
                    Logger?.LogError(new InvalidCastException(), "{}", exMsg);
                    return;
                }

                frame.SetObject(Opcode.Frame, payload, typeOfTypeInfo);

                //Write it and if it wrote perfectly fine, we will dequeue it
                Logger?.LogTrace("Sending payload: {}", payload.Command);
#if DEBUG
                Logger?.LogTrace("\t- Frame: {}", frame.Message);
#endif
                if (_namedPipe.WriteFrame(frame))
                {
                    //We sent it, so now dequeue it
                    Logger?.LogTrace("Sent Successfully.");
                    _rtQueue.TryDequeue(out _);
                }
                else
                {
                    //Something went wrong, so just give up and wait for the next time around.
                    Logger?.LogWarning("Something went wrong during writing!");
                    return;
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
        Logger?.LogTrace("Attempting to establish a handshake...");

        //We are establishing a lock and not releasing it until we sent the handshake message.
        // We need to set the key, and it would not be nice if someone did things between us setting the key.

        //Check its state
        if (State != RpcState.Disconnected)
        {
            Logger?.LogError("State must be disconnected in order to start a handshake!");
            return;
        }

        //Send it off to the server
        Logger?.LogTrace("Sending Handshake...");
        if (!_namedPipe?.WriteFrame(PipeFrame.Create(Opcode.Handshake, new Handshake { Version = Version, ClientID = _applicationID }, JsonSerializationContext.Default.Handshake)) ?? false)
        {
            Logger?.LogError("Failed to write a handshake.");
            return;
        }

        //This has to be done outside the lock
        SetConnectionState(RpcState.Connecting);
    }

    /// <summary>
    /// Establishes a farewell with the server by sending hand wave.
    /// </summary>
    private void SendHandwave()
    {
        Logger?.LogInformation("Attempting to wave goodbye...");

        //Check its state
        if (State == RpcState.Disconnected)
        {
            Logger?.LogError("State must NOT be disconnected in order to send a handwave!");
            return;
        }

        //Send a hand wave
        if (!_namedPipe?.WriteFrame(PipeFrame.Create(Opcode.Close, new Handshake { Version = Version, ClientID = _applicationID }, JsonSerializationContext.Default.Handshake)) ?? false)
        {
            Logger?.LogError("failed to write a handwave.");
        }
    }


    /// <summary>
    /// Attempts to connect to the pipe. Returns true on success
    /// </summary>
    /// <returns></returns>
    public bool AttemptConnection()
    {
        Logger?.LogInformation("Attempting a new connection");

        //The thread mustn't exist already
        if (_thread != null)
        {
            Logger?.LogError("Cannot attempt a new connection as the previous connection thread is not null!");
            return false;
        }

        //We have to be in the disconnected state
        if (State != RpcState.Disconnected)
        {
            Logger?.LogWarning("Cannot attempt a new connection as the previous connection hasn't changed state yet.");
            return false;
        }

        if (_aborting)
        {
            Logger?.LogError("Cannot attempt a new connection while aborting!");
            return false;
        }

        //Start the thread up
        _thread = new Thread(MainLoop)
        {
            Name = "Discord IPC Thread",
            IsBackground = true
        };
        _thread.Start();

        return true;
    }

    /// <summary>
    /// Sets the current state of the pipe, locking the l_states object for thread safety.
    /// </summary>
    /// <param name="state">The state to set it too.</param>
    private void SetConnectionState(RpcState state)
    {
        Logger?.LogTrace("Setting the connection state to {}", state.ToString().ToSnakeCase()?.ToUpperInvariant());
        lock (_lStates)
        {
            _state = state;
        }
    }

    /// <summary>
    /// Closes the connection and disposes of resources. This will not force termination, but instead allow Discord disconnect us after we say goodbye. 
    /// <para>This option helps prevents ghosting in applications where the process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence, and we are closing the connection manually, instead of waiting for the process to terminate.</para>
    /// </summary>
    public void Shutdown()
    {
        //Enable the flag
        Logger?.LogTrace("Initiated shutdown procedure");
        _shutdown = true;

        //Clear the commands and enqueue the close
        _rtQueue.Clear();
        if (ClearOnShutdown) _rtQueue.Enqueue(new PresenceCommand { Pid = _processID, Presence = null });
        _rtQueue.Enqueue(new CloseCommand());

        //Trigger the event
        _queueUpdatedEvent.Set();
    }

    /// <summary>
    /// Closes the connection and disposes of resources.
    /// </summary>
    public void Close()
    {
        if (_thread == null)
        {
            Logger?.LogError("Cannot close as it is not available!");
            return;
        }

        if (_aborting)
        {
            Logger?.LogError("Cannot abort as it has already been aborted");
            return;
        }

        //Set the abort state
        if (ShutdownOnly)
        {
            Shutdown();
            return;
        }

        //Terminate
        Logger?.LogTrace("Updating Abort State...");
        _aborting = true;
        _queueUpdatedEvent.Set();
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
    /// Connecting to the discord client. The handshake has been sent, and we are awaiting the ready event
    /// </summary>
    Connecting,

    /// <summary>
    /// We are connect to the client and can send and receive messages.
    /// </summary>
    Connected
}