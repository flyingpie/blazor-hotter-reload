using System.Reflection;
using System.Text;
using System.Text.Json;
using BlazorHMR.Protocol;

namespace BlazorHMR.UnitTest;

[TestClass]
public class ProtocolMessageTest
{
	private static readonly PatchChange SampleChange = new(
		Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
		[1, 2, 3],
		[4, 5, 6],
		[7, 8, 9],
		[10, 11]
	);

	[TestMethod]
	public void ServerMessagesKeepTheirTypeDiscriminator()
	{
		ServerMessage[] messages =
		[
			new HelloMessage(ProtocolVersion.Current, "session", 3, ["App.dll"], [], ["site.css"]),
			new CompilingMessage(),
			new NoChangesMessage(),
			new GenerationMessage(3, ["App.dll", "Lib.dll"]),
			new PatchMessage("session", 3, 1, [SampleChange]),
			new ErrorsMessage([new CompileDiagnostic("App.razor", 4, 2, "CS1002", "Expected ';'")]),
			new CssMessage("site.css"),
			new RebuiltMessage(),
			new ReloadMessage(),
		];

		foreach (var message in messages)
		{
			var json = Serialize(message);
			Assert.IsTrue(
				json.Contains($"\"type\":\"{TypeOf(message)}\"", StringComparison.Ordinal),
				$"{message.GetType().Name} did not keep its type discriminator: {json}"
			);

			var deserialized = JsonSerializer.Deserialize<ServerMessage>(json, ProtocolJson.Options);
			Assert.IsNotNull(deserialized);
			Assert.AreEqual(message.GetType(), deserialized.GetType(), json);
		}
	}

	[TestMethod]
	public void HelloRoundTripsItsPatches()
	{
		var hello = new HelloMessage(
			ProtocolVersion.Current,
			"session",
			3,
			["App.dll"],
			[new PatchMessage("session", 3, 1, [SampleChange]), new PatchMessage("session", 3, 2, [SampleChange])],
			["site.css"]
		);

		// Serialize as the base type, like the server does, so the top-level discriminator is included.
		var json = Serialize<ServerMessage>(hello);
		Assert.AreEqual(2, CountOccurrences(json, "\"type\":\"patch\""), json);

		var deserialized = Assert.IsInstanceOfType<HelloMessage>(
			JsonSerializer.Deserialize<ServerMessage>(json, ProtocolJson.Options)
		);
		Assert.AreEqual(ProtocolVersion.Current, deserialized.ProtocolVersion);
		Assert.AreEqual("session", deserialized.Session);
		Assert.AreEqual(3, deserialized.Generation);
		CollectionAssert.AreEqual(new[] { "App.dll" }, deserialized.Assemblies.ToArray());
		CollectionAssert.AreEqual(new[] { "site.css" }, deserialized.Css.ToArray());

		var patches = deserialized.Patches.OfType<PatchMessage>().ToList();
		Assert.AreEqual(2, patches.Count);
		Assert.AreEqual(1, patches[0].Sequence);
		Assert.AreEqual(2, patches[1].Sequence);
		Assert.AreEqual(SampleChange.ModuleId, patches[0].Changes[0].ModuleId);
		CollectionAssert.AreEqual(SampleChange.IL, patches[0].Changes[0].IL);
	}

	[TestMethod]
	public void ClientMessagesKeepTheirTypeDiscriminator()
	{
		ClientMessage[] messages =
		[
			new CapabilitiesMessage(ProtocolVersion.Current, true, "Baseline AddMethodToExistingType"),
			new RestartMessage(),
			new PatchFailedMessage(),
		];

		foreach (var message in messages)
		{
			var json = Serialize(message);
			Assert.IsTrue(
				json.Contains($"\"type\":\"{TypeOf(message)}\"", StringComparison.Ordinal),
				$"{message.GetType().Name} did not keep its type discriminator: {json}"
			);

			var deserialized = JsonSerializer.Deserialize<ClientMessage>(json, ProtocolJson.Options);
			Assert.IsNotNull(deserialized);
			Assert.AreEqual(message.GetType(), deserialized.GetType(), json);
		}
	}

	[TestMethod]
	public void CapabilitiesRoundTrip()
	{
		var message = new CapabilitiesMessage(ProtocolVersion.Current, true, "Baseline AddMethodToExistingType");

		var deserialized = Assert.IsInstanceOfType<CapabilitiesMessage>(
			JsonSerializer.Deserialize<ClientMessage>(Serialize<ClientMessage>(message), ProtocolJson.Options)
		);

		Assert.AreEqual(ProtocolVersion.Current, deserialized.ProtocolVersion);
		Assert.IsTrue(deserialized.Supported);
		Assert.AreEqual("Baseline AddMethodToExistingType", deserialized.Capabilities);
	}

	[TestMethod]
	public void MessageNamesAreUnique()
	{
		var names = typeof(MessageTypes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
			.Select(field => (string)field.GetRawConstantValue()!)
			.ToList();

		Assert.AreEqual(12, names.Count);
		CollectionAssert.AllItemsAreUnique(names);
	}

	private static string Serialize<T>(T message)
		where T : notnull =>
		Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options));

	private static string TypeOf(object message) =>
		message switch
		{
			HelloMessage => MessageTypes.Hello,
			CompilingMessage => MessageTypes.Compiling,
			NoChangesMessage => MessageTypes.NoChanges,
			GenerationMessage => MessageTypes.Generation,
			PatchMessage => MessageTypes.Patch,
			ErrorsMessage => MessageTypes.Errors,
			CssMessage => MessageTypes.Css,
			RebuiltMessage => MessageTypes.Rebuilt,
			ReloadMessage => MessageTypes.Reload,
			CapabilitiesMessage => MessageTypes.Capabilities,
			RestartMessage => MessageTypes.Restart,
			PatchFailedMessage => MessageTypes.PatchFailed,
			_ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType(), "Unknown protocol message"),
		};

	private static int CountOccurrences(string text, string value)
	{
		var count = 0;
		for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}
}
