using System.Reflection;

namespace Intersect.Framework.Reflection;

/// <summary>
/// Extension methods for <see cref="Assembly"/>.
/// </summary>
public static partial class AssemblyExtensions
{
    /// <inheritdoc cref="CreateInstanceOf{TParentType}(Assembly, Func{Type, bool}, object[])"/>
    public static TParentType CreateInstanceOf<TParentType>(this Assembly assembly, params object[] args) =>
        assembly.CreateInstanceOf<TParentType>(_ => true, args);

    /// <summary>
    /// Creates an instance of <typeparamref name="TParentType"/> given <paramref name="args"/>.
    /// </summary>
    /// <typeparam name="TParentType">the type to search for subtypes and create an instance of</typeparam>
    /// <param name="assembly">the <see cref="Assembly"/> to search for subtypes in</param>
    /// <param name="predicate">a function to filter subtypes with</param>
    /// <param name="args">the arguments to create the instance with</param>
    /// <returns>an instance of <typeparamref name="TParentType"/></returns>
    /// <exception cref="InvalidOperationException">if no matching subtypes are found, or instance creation fails</exception>
    public static TParentType CreateInstanceOf<TParentType>(this Assembly assembly, Func<Type, bool> predicate, params object[] args)
    {
        var validTypes = assembly.FindDefinedSubtypesOf<TParentType>();
        var type = validTypes.FirstOrDefault(predicate);

        if (type == default)
        {
            throw new InvalidOperationException($"Found no matching subtype of {typeof(TParentType).FullName} that can be created.");
        }

        if (Activator.CreateInstance(type, args) is TParentType instance)
        {
            return instance;
        }

        throw new InvalidOperationException($"Failed to create instance of {typeof(TParentType).FullName}.");
    }

    public static IEnumerable<Type> FindAbstractSubtypesOf(this Assembly assembly, Type type) =>
        assembly.FindSubtypesOf(type).Where(subtype => subtype?.IsAbstract ?? false);

    public static IEnumerable<Type> FindAbstractSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindAbstractSubtypesOf(typeof(TParentType));

    public static IEnumerable<Type> FindDefinedSubtypesOf(this Assembly assembly, Type type) =>
        assembly.FindSubtypesOf(type).Where(subtype => !(subtype == null || subtype.IsAbstract || subtype.IsGenericType || subtype.IsInterface));

    public static IEnumerable<Type> FindDefinedSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindDefinedSubtypesOf(typeof(TParentType));

    public static IEnumerable<Type> FindGenericSubtypesOf(this Assembly assembly, Type type) =>
        assembly.FindSubtypesOf(type).Where(subtype => subtype?.IsGenericType ?? false);

    public static IEnumerable<Type> FindGenericSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindGenericSubtypesOf(typeof(TParentType));

    public static IEnumerable<Type> FindInterfaceSubtypesOf(this Assembly assembly, Type type) =>
        assembly.FindSubtypesOf(type).Where(subtype => subtype?.IsInterface ?? false);

    public static IEnumerable<Type> FindInterfaceSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindInterfaceSubtypesOf(typeof(Type));

    public static IEnumerable<Type> FindSubtypesOf(this Assembly assembly, Type type) =>
        assembly.GetTypes().Where(type.IsAssignableFrom);

    public static IEnumerable<Type> FindSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindGenericSubtypesOf(typeof(TParentType));

    public static IEnumerable<Type> FindValueSubtypesOf(this Assembly assembly, Type type) =>
        assembly.FindSubtypesOf(type).Where(subtype => subtype?.IsValueType ?? false);

    public static IEnumerable<Type> FindValueSubtypesOf<TParentType>(this Assembly assembly) =>
        assembly.FindValueSubtypesOf(typeof(Type));

    public static bool TryFindResource(this Assembly assembly, string resourceName, out string? manifestResourceName)
    {
        if (assembly.IsDynamic)
        {
            manifestResourceName = default;
            return false;
        }

        manifestResourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name?.Contains(resourceName, StringComparison.CurrentCulture) ?? false);
        return manifestResourceName != default;
    }
}
