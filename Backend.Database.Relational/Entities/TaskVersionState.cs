namespace Backend.Database.Relational.Entities;

public enum TaskVersionState
{
    Registered,
    Active,
    Draining,
    Superseded,
    Failed
}
