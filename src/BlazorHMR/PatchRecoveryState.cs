using Microsoft.AspNetCore.Components;

namespace BlazorHMR;

/// <summary>
/// The state that an in-place patch leaves behind for a possible recovery full swap.
/// <para>
/// A patch can leave code that is out of date (for example when a method signature changed). The error boundary may then throw
/// parts of the component tree away before the full swap arrives. To still carry over the state of those components, the tree
/// is snapshotted before the patch. When the swap for that generation happens, it uses that snapshot instead of the live tree.
/// </para>
/// </summary>
internal sealed class PatchRecoveryState
{
	private readonly Lock _lock = new();
	private int _restartRequestedFor = -1;
	private List<IComponent>? _componentsBeforePatch;

	/// <summary>
	/// The component tree as it was before the current in-place patch; set by <see cref="InPlace.InPlacePatchApplier"/>.
	/// </summary>
	public List<IComponent>? ComponentsBeforePatch
	{
		get
		{
			lock (_lock)
			{
				return _componentsBeforePatch;
			}
		}
		set
		{
			lock (_lock)
			{
				_componentsBeforePatch = value;
			}
		}
	}

	/// <summary>
	/// Whether a full swap was already requested for <paramref name="generation"/>, so an error boundary loop can't keep asking.
	/// </summary>
	public bool IsRestartRequestedFor(int generation)
	{
		lock (_lock)
		{
			return _restartRequestedFor == generation;
		}
	}

	public void RequestRestart(int generation)
	{
		lock (_lock)
		{
			_restartRequestedFor = generation;
		}
	}

	/// <summary>
	/// The pre-patch component snapshot, if the swap for <paramref name="generation"/> is the recovery swap it was taken for.
	/// The snapshot is consumed either way: it belongs to one patch only.
	/// </summary>
	public List<IComponent>? TakeComponentsFor(int generation)
	{
		lock (_lock)
		{
			var components = _restartRequestedFor == generation ? _componentsBeforePatch : null;
			_componentsBeforePatch = null;
			return components;
		}
	}

	/// <summary>
	/// A page reload starts a new tree and a new generation count, so nothing from the previous page applies anymore.
	/// </summary>
	public void Reset()
	{
		lock (_lock)
		{
			_restartRequestedFor = -1;
			_componentsBeforePatch = null;
		}
	}
}
