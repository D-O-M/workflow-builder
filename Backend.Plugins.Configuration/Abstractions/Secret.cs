namespace Backend.Plugins.Configuration;

public readonly struct Secret<T>
{
    public Secret(T value)
    {
        Value = value;
    }

    public T Value { get; }

    public T Reveal() => Value;

    public override string ToString() => "********";
}
