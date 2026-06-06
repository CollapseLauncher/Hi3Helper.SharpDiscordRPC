using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
// ReSharper disable NonReadonlyMemberInGetHashCode

namespace DiscordRPC.IO;

/// <summary>
/// A frame received and sent to the Discord client for RPC communications.
/// </summary>
public class PipeFrame : IEquatable<PipeFrame>
{
    /// <summary>
    /// The maximum size of a pipe frame (16kb).
    /// </summary>
    public static readonly int MaxSize = 16 * 1024;

    /// <summary>
    /// The opcode of the frame
    /// </summary>
    public Opcode Opcode { get; set; }

    /// <summary>
    /// The length of the frame data
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// The data in the frame
    /// </summary>
    public byte[] Data
    {
        get;
        private set;
    }

    /// <summary>
    /// The data represented as a string.
    /// </summary>
    public string Message
    {
        get => MessageEncoding.GetString(Data);
        set
        {
            int        maxDataBytes = MessageEncoding.GetByteCount(value);
            Span<byte> data         = SetData(maxDataBytes);
            Length = MessageEncoding.GetBytes(value, data);
        }
    }

    private PipeFrame(Opcode opcode)
    {
        Opcode = opcode;
        Data   = [];
        Length = 0;
    }

    /// <summary>
    /// Creates a new pipe frame instance
    /// </summary>
    /// <param name="opcode">The opcode of the frame</param>
    /// <param name="data">The data of the frame that will be serialized as JSON</param>
    /// <param name="jsonTypeInfo">The JSON type info of the data</param>
    public static PipeFrame Create<T>(Opcode opcode, T data, JsonTypeInfo<T> jsonTypeInfo)
        where T : class
    {
        PipeFrame frame = new(opcode);
        frame.SetObject(data, jsonTypeInfo);

        return frame;
    }

    public static PipeFrame Create() => new(default);

    /// <summary>
    /// Gets the encoding used for the pipe frames
    /// </summary>
    private static Encoding MessageEncoding => Encoding.UTF8;

    /// <summary>
    /// Sets the data to a new byte array of the specified length. If there is already data, it will be freed first.
    /// If <paramref name="length"/> is 0, the data will be freed and unallocated.
    /// </summary>
    /// <param name="length">The length of the new data buffer</param>
    /// <returns>A span representing the new data buffer</returns>
    private Span<byte> SetData(int length)
    {
        if (length == 0)
        {
            Data   = [];
            Length = 0;
            return Span<byte>.Empty;
        }

        Data   = GC.AllocateUninitializedArray<byte>(length);
        Length = length;
        return new Span<byte>(Data, 0, length);
    }

    /// <summary>
    /// Sets the opcodes and serializes the object into a json string.
    /// </summary>
    /// <param name="opcode"></param>
    /// <param name="obj"></param>
    /// <param name="jsonTypeInfo"></param>
    public void SetObject(Opcode opcode, object obj, JsonTypeInfo jsonTypeInfo)
    {
        Opcode = opcode;
        SetObject(obj, jsonTypeInfo);
    }

    /// <summary>
    /// Serializes the object into json string then encodes it into <see cref="Data"/>.
    /// </summary>
    /// <param name="obj"></param>
    /// <param name="jsonTypeInfo"></param>
    public void SetObject(object obj, JsonTypeInfo jsonTypeInfo) =>
        Message = JsonSerializer.Serialize(obj, jsonTypeInfo);

    /// <summary>
    /// Deserializes the data into the supplied type using JSON.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into</typeparam>
    /// <returns></returns>
    public T? GetObject<T>(JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(Message, typeInfo);

    /// <summary>
    /// Attempts to read the contents of the frame from the stream
    /// </summary>
    public bool ReadBuffer(scoped Span<byte> frameBuffer)
    {
        if (frameBuffer.Length < 8)
        {
            return false;
        }

        Opcode = MemoryMarshal.Read<Opcode>(frameBuffer);
        Length = MemoryMarshal.Read<int>(frameBuffer[4..]);

        frameBuffer = frameBuffer.Slice(8, Length);
        scoped Span<byte> dataSpan = SetData(Length);

        if (dataSpan.Length >= frameBuffer.Length &&
            frameBuffer.TryCopyTo(dataSpan))
        {
            return true;
        }

        SetData(0);
        return false;
    }

    /// <summary>
    /// Writes the frame into the target frame as one big byte block.
    /// </summary>
    /// <param name="stream"></param>
    public void WriteStream(Stream stream)
    {
        using BinaryWriter writer = new(stream, MessageEncoding, true);
        writer.Write((int)Opcode);
        writer.Write(Length);
        writer.Write(Data);
    }

    /// <summary>
    /// Compares if the frame equals the other frame.
    /// </summary>
    public bool Equals(PipeFrame? other) => Opcode == other?.Opcode &&
                                            Length == other.Length &&
                                            (Data == other.Data ||
                                             Data.SequenceEqual(other.Data));

    /// <summary>
    /// Compares if the frame equals the other frame.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is PipeFrame other &&
        Equals(other);

    public override int GetHashCode() => HashCode.Combine(Opcode, Length, Data);
}
