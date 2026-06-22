namespace Generics;

// Pulled into the slice via Repository<T>'s `where T : IEntity` constraint.
public interface IEntity
{
    int Id { get; }
}
