using BlazorHMR.FullSwap;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Threading.Tasks;

namespace BlazorHMR.UnitTest;

[TestClass]
public class GenerationFactoryTest
{
	[TestMethod]
	public async Task ForwardsHostServicesAsTheSameInstancesInRegistrationOrder()
	{
		await using var host = new HostTier();
		var hostServices = new ServiceCollection();
		hostServices.AddSingleton<IGreeter>(_ => new Greeter("first"));
		hostServices.AddSingleton<IGreeter>(_ => new Greeter("second"));
		hostServices.AddSingleton(_ => new Counter());
		host.InitializeDesktop(hostServices);

		var factory = new GenerationFactory(
			new AppEntryPoint(typeof(TestStartup).FullName!, nameof(TestStartup.ConfigureServices)),
			host
		);
		var assembly = typeof(TestStartup).Assembly;

		await using var generation = factory.Build(
			0,
			assembly,
			new Dictionary<string, Assembly> { [assembly.GetName().Name!] = assembly }
		);

		// The host resolves the registrations in order, and the generation gets those exact instances (registered as
		// instances, so the generation's container doesn't own them).
		var hostGreeters = host.Services.GetServices<IGreeter>().ToList();
		var generationGreeters = generation.Services.GetServices<IGreeter>().ToList();
		CollectionAssert.AreEqual(new[] { "first", "second" }, generationGreeters.Select(g => g.Name).ToList());
		Assert.AreEqual(hostGreeters.Count, generationGreeters.Count);
		for (var i = 0; i < hostGreeters.Count; i++)
		{
			Assert.AreSame(hostGreeters[i], generationGreeters[i]);
		}

		Assert.AreSame(host.Services.GetRequiredService<Counter>(), generation.Services.GetRequiredService<Counter>());
	}
}

public interface IGreeter
{
	string Name { get; }
}

public sealed class Greeter(string name) : IGreeter
{
	public string Name { get; } = name;
}

public sealed class Counter
{
}

public static class TestStartup
{
	public static void ConfigureServices(IServiceCollection services) { }
}
