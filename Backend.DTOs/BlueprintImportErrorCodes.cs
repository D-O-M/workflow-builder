namespace Backend.Models;

public static class BlueprintImportErrorCodes
{
    public const string UnsupportedSchemaVersion = "blueprint.import.schema.unsupported";
    public const string DefinitionKeyMissing = "blueprint.import.definitionKey.missing";
    public const string DisplayNameMissing = "blueprint.import.displayName.missing";
    public const string StartingTaskMissing = "blueprint.import.start.missing";
    public const string TaskKeyDuplicate = "blueprint.import.taskKey.duplicate";
    public const string NodeKindInvalid = "blueprint.import.nodeKind.invalid";
    public const string CollectionIteratorItemKindInvalid = "blueprint.import.collectionIterator.itemKind.invalid";
    public const string TaskDefinitionMissing = "blueprint.import.taskDefinition.missing";
    public const string TaskLaneMissing = "blueprint.import.taskLane.missing";
    public const string ChannelMissing = "blueprint.import.channel.missing";
}
