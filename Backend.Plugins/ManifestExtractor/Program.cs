using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        // args[0]: Path to plugin DLL
        // args[1]: Fully qualified manifest class name, e.g. GeneratedPluginManifests.MyPlugin_Manifest
        // args[2]: Output yaml file path

        var asm = Assembly.LoadFrom(args[0]);
        var manifestType = asm.GetType(args[1]);
        if (manifestType == null)
        {
            Console.Error.WriteLine($"Type not found: {args[1]}");
            Environment.Exit(2);
        }
        var yamlProp = manifestType.GetProperty("Yaml", BindingFlags.Static | BindingFlags.Public);
        if (yamlProp == null)
        {
            Console.Error.WriteLine($"Yaml property not found on {args[1]}");
            Environment.Exit(3);
        }
        var yaml = (string)yamlProp.GetValue(null);
        File.WriteAllText(args[2], yaml);
        Console.WriteLine($"Wrote: {args[2]}");
    }
}
