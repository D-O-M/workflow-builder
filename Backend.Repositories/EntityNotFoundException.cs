namespace Backend.Repositories;

public abstract class EntityNotFoundException : Exception
{
    public EntityNotFoundException(string? message) : base(message)
    {
    }

    public EntityNotFoundException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

public class EntityNotFoundException<TEntity> : EntityNotFoundException
{
    public EntityNotFoundException(int id)
        : base($"Entity of type '{typeof(TEntity).Name}' with ID '{id}' could not be found.")
    {
    }

    public EntityNotFoundException(int id, Exception? innerException)
        : base($"Entity of type '{typeof(TEntity).Name}' with ID '{id}' could not be found.",
            innerException)
    {
    }
}
