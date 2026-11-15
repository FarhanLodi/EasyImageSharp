using System.Buffers;

namespace EasyImageSharp.Memory;

/// <summary>Owns a plain managed array that nobody takes back; disposing it only makes the memory unreachable through it.</summary>
internal sealed class ArrayMemoryOwner<T> : IMemoryOwner<T>
{
    private readonly int length;
    private T[]? array;

    public ArrayMemoryOwner(T[] array, int length)
    {
        this.array = array;
        this.length = length;
    }

    public Memory<T> Memory
    {
        get
        {
            T[] current = Volatile.Read(ref this.array) ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner<T>));
            return current.AsMemory(0, this.length);
        }
    }

    public void Dispose() => Volatile.Write(ref this.array, null);
}
