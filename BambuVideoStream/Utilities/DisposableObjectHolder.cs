using System;

namespace BambuVideoStream.Utilities;

/// <summary>
/// Allows executing additional actions when an object is disposed
/// </summary>
/// <remarks>Provides 2 options for handling additional actions: An event and an Action.</remarks>
/// <typeparam name="T"></typeparam>
internal class DisposableObjectHolder<T>(T theObject) : IDisposable
{
    public T Value => theObject;

    public event EventHandler<T>? Disposing;

    public void Dispose()
    {
        this.Disposing?.Invoke(this, theObject);
        if (theObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
