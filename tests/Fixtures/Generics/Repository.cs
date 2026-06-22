namespace Generics;

// Open generic seed. The only reference to IEntity is through the type-parameter
// constraint, so collecting IEntity.cs proves the constraint walker works.
public class Repository<T> where T : IEntity
{
    private readonly List<T> _items = new();

    public void Add(T entity) => _items.Add(entity);

    public T GetById(int id)
    {
        foreach (var item in _items)
        {
            if (item.Id == id)
            {
                return item;
            }
        }
        return default;
    }
}
