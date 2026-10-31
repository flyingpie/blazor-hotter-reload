namespace BlazorHMR.UnitTest;

/// <summary>
/// A base class for test types that is never swapped itself (like a class in a library that didn't change).
/// </summary>
public abstract class UnswappedBase(string name)
{
	public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}
