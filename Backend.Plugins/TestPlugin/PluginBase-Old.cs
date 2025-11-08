using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestPlugin.Old;

internal enum ChannelMode
{
    Input,
    Output
}

internal abstract record ChannelParameter(string Name, string Description);

internal record ChannelParameter<T> : ChannelParameter
{
    public T Value { get; set; }

    public ChannelParameter(string name, string description, T value) : base(name, description)
    {
        Value = value;
    }
}

internal record Channel(string Name, ChannelMode Mode, ChannelParameter[] Parameters);

internal record InputChannel(string Name, ChannelParameter[] Parameters) : Channel(Name, ChannelMode.Input, Parameters);

internal record OutputChannel(string Name, ChannelParameter[] Parameters) : Channel(Name, ChannelMode.Output, Parameters)
{
    public void Write<T>(ChannelParameter<T> channelParameter, T value)
    {
        Console.WriteLine($"Sending data from output channel '{Name}': data");
    }
}

internal class PluginBase
{
    public string Name { get; set; }
    public string Version { get; set; }
    public Channel[] InputChannels { get; set; } = [];
    public Channel[] OutputChannels { get; set; } = [];

    public PluginBase(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public virtual void Initialize()
    {
        Console.WriteLine($"{Name} plugin initialized.");
    }

    public virtual void Execute()
    {
        Console.WriteLine($"{Name} plugin executed.");
    }

    public virtual void Shutdown()
    {
        Console.WriteLine($"{Name} plugin shut down.");
    }
}
