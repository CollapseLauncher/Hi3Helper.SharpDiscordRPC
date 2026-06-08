using Microsoft.Extensions.Logging;
using System;
using System.IO.Pipes;
using System.Threading;
using System.IO;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Tasks;
// ReSharper disable UnusedMember.Global

#pragma warning disable CA1873
namespace DiscordRPC.IO;

/// <summary>
/// A named pipe client using the .NET framework <see cref="NamedPipeClientStream"/>
/// </summary>
public sealed class ManagedNamedPipeClient
{
    /// <summary>
    /// The logger for the Pipe client to use
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Checks if the client is connected
    /// </summary>
    public bool IsConnected
    {
        get
        {
            //This will trigger if the stream is disabled. This should prevent the lock check
            if (_isClosed) return false;

            //We cannot be sure it's still connected, so lets double check
            return _stream is { IsConnected: true };
        }
    }

    /// <summary>
    /// The pipe we are currently connected too.
    /// </summary>
    public int ConnectedPipe { get; private set; }

    private          NamedPipeClientStream? _stream;
    private readonly byte[]                 _buffer;

    private readonly ConcurrentQueue<PipeFrame> _frameQueue = new();

    private volatile bool _isDisposed;
    private volatile bool _isClosed = true;

    /// <summary>
    /// Creates a new instance of a Managed NamedPipe client. Doesn't connect to anything yet, just setups the values.
    /// </summary>
    public ManagedNamedPipeClient()
    {
        _buffer = ArrayPool<byte>.Shared.Rent(PipeFrame.MaxSize);
        _stream = null;
    }

    ~ManagedNamedPipeClient() => Dispose();

    /// <summary>
    /// Connects to the pipe
    /// </summary>
    public async Task<bool> TryConnectAsync(int pipe)
    {
        Logger?.LogTrace("ManagedNamedPipeClient.Connection({})", pipe);

        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (pipe > 9)
            throw new ArgumentOutOfRangeException(nameof(pipe), "Argument cannot be greater than 9");

        int startPipe = 0;
        if (pipe >= 0)
            startPipe = pipe;

        foreach (string pipeName in PipeLocation.GetPipes(startPipe))
        {
            if (!await AttemptConnectionAsync(pipeName))
            {
                continue;
            }

            BeginReadStream();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts a new connection
    /// </summary>
    /// <returns></returns>
    private async Task<bool> AttemptConnectionAsync(string pipeName)
    {
        try
        {
            //Create the client
            Logger?.LogInformation("Attempting to connect to '{}'", pipeName);
            _stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Intentionally use a timeout of 0 here to avoid spinlock overhead.
            // We are already performing local retry logic, so this is not required.
            await _stream.ConnectAsync(1000);

            //Spin for a bit while we wait for it to finish connecting
            Logger?.LogTrace("Waiting for connection...");
            while (!_stream.IsConnected)
            {
                await Task.Delay(100);
            }

            //Store the value
            Logger?.LogInformation("Connected to '{}'", pipeName);
            ConnectedPipe = int.Parse(pipeName[pipeName.LastIndexOf('-')..]); // TODO: Deprecate this
            _isClosed     = false;
        }
        catch (Exception e)
        {
            //Something happened, try again
            //TODO: Log the failure condition
            Logger?.LogError("Failed connection to {pipe}. {msg}", pipeName, e.Message);
            Close();
        }

        Logger?.LogTrace("Done. Result: {}", _isClosed);
        return !_isClosed;
    }

    /// <summary>
    /// Starts a read. Can be executed in another thread.
    /// </summary>
    private void BeginReadStream()
    {
        if (_isClosed) return;

        try
        {
            // Make sure the stream is valid
            if (!(_stream?.IsConnected ?? false)) return;

            Logger?.LogTrace("Beginning Read of {} bytes", _buffer.Length);
            _stream?.BeginRead(_buffer, 0, _buffer.Length, EndReadStream, _stream.IsConnected);
        }
        catch (ObjectDisposedException)
        {
            Logger?.LogWarning("Attempted to start reading from a disposed pipe");
        }
        catch (InvalidOperationException)
        {
            //The pipe has been closed
            Logger?.LogWarning("Attempted to start reading from a closed pipe");
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "An exception occured while starting to read a stream: {}", e.Message);
        }
    }

    /// <summary>
    /// Ends a read. Can be executed in another thread.
    /// </summary>
    /// <param name="callback"></param>
    private void EndReadStream(IAsyncResult callback)
    {
        Logger?.LogTrace("Ending Read");
        int bytes;

        try
        {
            // Attempt to read the bytes, catching for IO exceptions or dispose exceptions
            // Make sure the stream is still valid
            if (_stream is not { IsConnected: true }) return;

            // Read our bytes
            bytes = _stream.EndRead(callback);
        }
        catch (IOException)
        {
            Logger?.LogWarning("Attempted to end reading from a closed pipe");
            return;
        }
        catch (NullReferenceException)
        {
            Logger?.LogWarning("Attempted to read from a null pipe");
            return;
        }
        catch (ObjectDisposedException)
        {
            Logger?.LogWarning("Attempted to end reading from a disposed pipe");
            return;
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "An exception occured while ending a read of a stream: {}", e.Message);
            return;
        }

        // How much did we read?
        Logger?.LogTrace("Read {} bytes", bytes);

        // Did we read anything? If we did we should enqueue it.
        if (bytes > 0)
        {
            // Load it into a memory stream and read the frame
            try
            {
                PipeFrame frame = PipeFrame.Create();
                if (frame.ReadBuffer(_buffer.AsSpan(0, bytes)))
                {
                    Logger?.LogTrace("Read a frame: {}", frame.Opcode);

                    //Enqueue the stream
                    _frameQueue.Enqueue(frame);
                }
                else
                {
                    //TODO: Enqueue a pipe close event here as we failed to read something.
                    Logger?.LogError("Pipe failed to read from the data received by the stream.");
                    Close();
                }
            }
            catch (Exception e)
            {
                Logger?.LogError("A exception has occured while trying to parse the pipe data: {}", e.Message);
                Close();
            }
        }
        else
        {
            //If we read 0 bytes, it's probably a broken pipe. However, I have only confirmed this is the case for MacOSX.
            Logger?.LogError("Empty frame was read on {}, aborting.", Environment.OSVersion);
            Close();
        }

        if (_isClosed || !IsConnected)
        {
            return;
        }

        //We are still connected, so continue to read
        Logger?.LogTrace("Starting another read");
        BeginReadStream();
    }

    /// <summary>
    /// Reads a frame, returning false if none are available
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public bool ReadFrame(out PipeFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        //Check the queue, returning the pipe if we have anything available. Otherwise, null.
        if (!_frameQueue.IsEmpty)
        {
            return _frameQueue.TryDequeue(out frame);
        }

        //We found nothing, so just default and return null
        frame = PipeFrame.Create();
        return false;
    }

    /// <summary>
    /// Writes a frame to the pipe
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public bool WriteFrame(PipeFrame frame)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        //Write the frame. We are assuming proper duplex connection here
        if (_isClosed || !IsConnected)
        {
            Logger?.LogError("Failed to write frame because the stream is closed");
            return false;
        }

        try
        {
            //Write the pipe
            //This can only happen on the main thread so it should be fine.
            if (_stream != null)
            {
                frame.WriteStream(_stream);
            }

            return true;
        }
        catch (IOException io)
        {
            Logger?.LogError(io, "Failed to write frame because of a IO Exception: {}", io.Message);
        }
        catch (ObjectDisposedException)
        {
            Logger?.LogWarning("Failed to write frame as the stream was already disposed");
        }
        catch (InvalidOperationException)
        {
            Logger?.LogWarning("Failed to write frame because of a invalid operation");
        }

        //We must have failed the try catch
        return false;
    }

    /// <summary>
    /// Closes the pipe
    /// </summary>
    public void Close()
    {
        //If we are already closed, just exit
        if (_isClosed)
        {
            Logger?.LogWarning("Tried to close a already closed pipe.");
            return;
        }

        //flush and dispose
        try
        {
            //Wait for the stream object to become available.
            if (_stream != null)
            {
                try
                {
                    //Stream isn't null, so flush it and then dispose of it.\
                    // We are doing a catch here because it may throw an error during this process, and we don't care if it fails.
                    _stream.Flush();
                    _stream.Dispose();
                }
                catch (Exception)
                {
                    //We caught an error, but we don't care anyway because we are disposing of the stream.
                }

                //Make the stream null and set our flag.
                _stream   = null;
                _isClosed = true;
            }
            else
            {
                //The stream is already null?
                Logger?.LogWarning("Stream was closed, but no stream was available to begin with!");
            }
        }
        catch (ObjectDisposedException)
        {
            //its already been disposed
            Logger?.LogWarning("Tried to dispose already disposed stream");
        }
        finally
        {
            //For good measures, we will mark the pipe as closed anyway
            _isClosed = true;
        }
    }

    /// <summary>
    /// Disposes of the stream
    /// </summary>
    public void Dispose()
    {
        //Prevent double disposing
        //Also set our dispose flag atomically
        if (Interlocked.Exchange(ref _isDisposed, true)) return;

        //Close the stream (disposing of it too)
        if (!_isClosed) Close();

        //Dispose of the stream if it hasn't been destroyed already.
        Stream? prevStream = Interlocked.Exchange(ref _stream, null);
        prevStream?.Dispose();

        ArrayPool<byte>.Shared.Return(_buffer);
        GC.SuppressFinalize(this);
    }
}