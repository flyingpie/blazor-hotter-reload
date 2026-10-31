using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;

namespace BlazorHMR.Blazor;

/// <summary>
/// Blazor allows only one <see cref="SectionOutlet"/> per section ID. A hot reload render can insert the new outlet before
/// Blazor disposes the old one (for example when a keyed subtree is replaced, or when an outlet moves to a different sequence),
/// so the new outlet would fail with "There is already a subscriber...". Clearing the subscriptions first lets the new tree
/// subscribe; the old outlets' <see cref="SectionOutlet.Dispose"/> then becomes a no-op. Providers are left untouched, so the
/// section content stays available.
/// </summary>
[SuppressMessage(
	"Major Code Smell",
	"S3011:Reflection should not be used to increase accessibility",
	Justification = "Dev-only tooling, poking Blazor internals is the point"
)]
public static class SectionSubscriptions
{
	private const string RegistryFieldName = "_sectionRegistry";
	private const string SubscribersFieldName = "_subscribersByIdentifier";
	private const string SubscribedIdentifierFieldName = "_subscribedIdentifier";

	private static bool _warned;

	/// <summary>
	/// Drops the dispatcher's current section subscriptions, but keeps the providers. Must run on the dispatcher, immediately
	/// before the render that recreates or moves the outlets.
	/// </summary>
	public static void Reset(Dispatcher dispatcher)
	{
		var registryField = typeof(Dispatcher).GetField(RegistryFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		if (registryField is null)
		{
			Warn($"field 'Dispatcher.{RegistryFieldName}' was not found");
			return;
		}

		var registry = registryField.GetValue(dispatcher);
		if (registry is null)
		{
			// Nothing has subscribed yet, so there is nothing to reset.
			return;
		}

		var subscribersField = registry.GetType().GetField(SubscribersFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		var subscribedIdentifierField = typeof(SectionOutlet).GetField(
			SubscribedIdentifierFieldName,
			BindingFlags.Instance | BindingFlags.NonPublic
		);
		if (subscribersField is null || subscribedIdentifierField is null)
		{
			Warn(
				subscribersField is null
					? $"field 'SectionRegistry.{SubscribersFieldName}' was not found"
					: $"field 'SectionOutlet.{SubscribedIdentifierFieldName}' was not found"
			);
			return;
		}

		if (subscribersField.GetValue(registry) is not IDictionary subscribers)
		{
			Warn($"field 'SectionRegistry.{SubscribersFieldName}' is no longer a dictionary");
			return;
		}

		// Make the old outlets' disposal a no-op before clearing the registry: otherwise they would try to unsubscribe a second time.
		foreach (var subscriber in subscribers.Values)
		{
			if (subscriber is SectionOutlet outlet)
			{
				subscribedIdentifierField.SetValue(outlet, null);
			}
		}

		subscribers.Clear();
	}

	/// <summary>
	/// One warning is enough: a changed Blazor internal will keep failing until this library is updated, and every hot reload
	/// would otherwise flood the console.
	/// </summary>
	private static void Warn(string message)
	{
		if (_warned)
		{
			return;
		}

		_warned = true;
		Console.WriteLine(
			$"[hotreload] Could not reset section subscriptions, layout changes may fail: {message}. "
				+ "Blazor internals have changed, BlazorHMR needs to be updated."
		);
	}
}
