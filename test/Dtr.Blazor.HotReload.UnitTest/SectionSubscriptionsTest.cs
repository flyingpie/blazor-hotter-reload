using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dtr.Blazor.HotReload.UnitTest;

[TestClass]
public class SectionSubscriptionsTest
{
	private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	[TestMethod]
	public void ResetsBeforeAnOutletMovesToANewSequence()
	{
		var renderer = new TestRenderer();
		var root = new Layout();
		var componentId = renderer.Dispatcher.InvokeAsync(() => renderer.Attach(root)).GetAwaiter().GetResult();
		renderer.Dispatcher.InvokeAsync(() => renderer.RenderRoot(componentId)).GetAwaiter().GetResult();
		AssertNoErrors(renderer);

		// Removing the preceding indicator shifts the outlet to a different sequence. The diff inserts the new outlet before
		// disposing the old one, which would throw without resetting the subscriptions first.
		renderer
			.Dispatcher.InvokeAsync(() =>
			{
				SectionSubscriptions.Reset(renderer.Dispatcher);
				root.ToggleIndicator();
			})
			.GetAwaiter()
			.GetResult();

		AssertNoErrors(renderer);
	}

	[TestMethod]
	public void DropsSubscribersButKeepsProviders()
	{
		var dispatcher = Dispatcher.CreateDefault();
		var registry = GetRegistry(dispatcher);
		const string identifier = "main-header";
		var content = new SectionContent();
		registry.GetType().GetMethod("AddProvider", AnyInstance)!.Invoke(registry, [identifier, content, false]);

		var subscribers = GetSubscribers(registry);
		var subscribedIdentifierField = typeof(SectionOutlet).GetField("_subscribedIdentifier", AnyInstance)!;
		var first = new SectionOutlet();
		var second = new SectionOutlet();
		subscribers.Add(identifier, first);
		subscribers.Add("other", second);
		subscribedIdentifierField.SetValue(first, identifier);
		subscribedIdentifierField.SetValue(second, "other");

		SectionSubscriptions.Reset(dispatcher);

		Assert.AreEqual(0, subscribers.Count);
		Assert.IsNull(subscribedIdentifierField.GetValue(first), "The old outlet no longer owns the subscription");
		Assert.IsNull(subscribedIdentifierField.GetValue(second), "The old outlet no longer owns the subscription");

		var providers = (IDictionary)registry.GetType().GetField("_providersByIdentifier", AnyInstance)!.GetValue(registry)!;
		Assert.AreEqual(1, providers.Count, "Resetting subscriptions must not remove the section content");
		var providerList = (IList)providers[identifier]!;
		Assert.AreEqual(1, providerList.Count);
		Assert.AreSame(content, providerList[0]);

		// Disposing the old outlets after the reset must not try to unsubscribe them a second time.
		first.Dispose();
		second.Dispose();
	}

	[TestMethod]
	public void DoesNothingBeforeTheRegistryIsCreated()
	{
		SectionSubscriptions.Reset(Dispatcher.CreateDefault());
	}

	private static object GetRegistry(Dispatcher dispatcher) =>
		typeof(Dispatcher).GetProperty("SectionRegistry", AnyInstance)!.GetValue(dispatcher)!;

	private static IDictionary GetSubscribers(object registry) =>
		(IDictionary)registry.GetType().GetField("_subscribersByIdentifier", AnyInstance)!.GetValue(registry)!;

	private static void AssertNoErrors(TestRenderer renderer) =>
		Assert.AreEqual(0, renderer.Errors.Count, string.Join(Environment.NewLine, renderer.Errors));

	[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "A test renderer is the point")]
	private sealed class TestRenderer : Renderer
	{
		public TestRenderer()
			: base(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance) { }

		public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

		public List<Exception> Errors { get; } = [];

		public int Attach(IComponent component) => AssignRootComponentId(component);

		public Task RenderRoot(int componentId) => RenderRootComponentAsync(componentId);

		protected override void HandleException(Exception exception) => Errors.Add(exception);

		protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
	}

	[SuppressMessage("Usage", "BL0006:Do not use RenderTree types", Justification = "A test component is the point")]
	private sealed class Layout : ComponentBase
	{
		private bool _showIndicator = true;

		public void ToggleIndicator()
		{
			_showIndicator = false;
			StateHasChanged();
		}

		[SuppressMessage(
			"Usage",
			"ASP0006:Do not use non-literal sequence numbers",
			Justification = "The shifted sequence is the scenario under test"
		)]
		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			if (_showIndicator)
			{
				builder.OpenElement(0, "span");
				builder.AddContent(1, "indicator");
				builder.CloseElement();
			}

			builder.OpenComponent<SectionOutlet>(_showIndicator ? 4 : 2);
			builder.AddComponentParameter(_showIndicator ? 5 : 3, nameof(SectionOutlet.SectionName), "main-header");
			builder.CloseComponent();
		}
	}
}
