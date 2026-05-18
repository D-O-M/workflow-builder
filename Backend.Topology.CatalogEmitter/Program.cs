using Backend.Topology;

var outputPath = args.FirstOrDefault();
var json = WorkflowBuilderTopology.CreateCatalogJson();

if (string.IsNullOrWhiteSpace(outputPath))
{
    Console.Write(json);
    return 0;
}

var fullPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
await File.WriteAllTextAsync(fullPath, json);
Console.WriteLine($"Wrote topology catalog to {fullPath}.");
return 0;
