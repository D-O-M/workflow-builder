using System.Linq.Expressions;
using System.Reflection;

namespace Backend.Plugins.Harness;

public sealed class PropertySetterUtility<T, TProp>
{
    private readonly Action<T, TProp> _set;

    public PropertySetterUtility(Expression<Func<T, TProp>> selector)
    {
        _set = CreateSetter(selector);
    }

    public void Set(T target, TProp value) => _set(target, value);

    private static Action<T, TProp> CreateSetter(Expression<Func<T, TProp>> selector)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));

        var body = selector.Body is UnaryExpression u && u.NodeType == ExpressionType.Convert
            ? u.Operand
            : selector.Body;

        if (body is not MemberExpression m || m.Member is not PropertyInfo prop)
            throw new ArgumentException("Selector must be a simple property access: x => x.Property", nameof(selector));

        if (!prop.CanWrite)
            throw new ArgumentException($"Property '{prop.Name}' is read-only.", nameof(selector));

        // Build: (T obj, TProp value) => obj.Prop = value;
        var objParam = Expression.Parameter(typeof(T), "obj");
        var valParam = Expression.Parameter(typeof(TProp), "value");

        // If the property is declared on a base type/interface, Expression.Property(objParam, prop)
        // still works provided the runtime type is compatible.
        var assign = Expression.Assign(Expression.Property(objParam, prop), valParam);

        return Expression.Lambda<Action<T, TProp>>(assign, objParam, valParam).Compile();
    }
}
