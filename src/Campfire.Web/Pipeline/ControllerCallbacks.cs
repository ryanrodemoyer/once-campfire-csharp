using System.Collections.Immutable;

namespace Campfire.Web.Pipeline;

/// <summary>
/// A controller's action callbacks (<c>AbstractController::Callbacks</c> over
/// <c>ActiveSupport::Callbacks</c>), built the way a Rails class body declares them. Each call
/// returns a new chain, so a subclass starts from its parent's with <see cref="For{TDerived}"/>
/// and the parent's is never changed.
/// <list type="bullet">
/// <item><c>before_action</c>/<c>after_action</c> append; naming a callback that is already in the
/// chain moves it to the end with the new conditions (<c>CallbackChain#append</c>).</item>
/// <item><c>only:</c> and <c>except:</c> become <c>if:</c> and <c>unless:</c> conditions on the
/// action name.</item>
/// <item><c>skip_*_action</c> without conditions removes the callback; with them it keeps the
/// callback and adds the inverted conditions (<c>Callback#merge_conditional_options</c>). Skipping
/// a callback that isn't there throws, as Rails raises.</item>
/// <item>A before callback that renders or redirects (<see cref="Controller.Performed"/>) halts
/// the chain: the rest, the action and the after callbacks don't run.</item>
/// <item>After callbacks run in reverse order of declaration.</item>
/// </list>
/// </summary>
public sealed class ControllerCallbacks<TController> where TController : Controller
{
    readonly ImmutableArray<Callback> callbacks;
    readonly ImmutableArray<Rescue> rescues;

    internal ControllerCallbacks(ImmutableArray<Callback> callbacks, ImmutableArray<Rescue> rescues)
    {
        this.callbacks = callbacks;
        this.rescues = rescues;
    }

    /// <summary>The names of the before callbacks, in order (for tests and debugging).</summary>
    public IEnumerable<string?> BeforeNames => callbacks.Where(callback => callback.Kind == CallbackKind.Before).Select(callback => callback.Name);

    /// <summary>A subclass's chain, starting as this one.</summary>
    public ControllerCallbacks<TDerived> For<TDerived>() where TDerived : TController =>
        new(
            [.. callbacks.Select(callback => new ControllerCallbacks<TDerived>.Callback(
                callback.Name,
                callback.Kind,
                callback.Run,
                [.. callback.If.Select(condition => (Func<TDerived, bool>)condition)],
                [.. callback.Unless.Select(condition => (Func<TDerived, bool>)condition)]))],
            [.. rescues.Select(rescue => new ControllerCallbacks<TDerived>.Rescue(rescue.Type, (controller, error) => rescue.Handle(controller, error)))]);

    /// <summary><c>before_action name, only:, except:, if:, unless:</c>; an unnamed callback is a block.</summary>
    public ControllerCallbacks<TController> Before(
        string? name,
        Func<TController, ValueTask> callback,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        Append(name, CallbackKind.Before, callback, only, except, @if, unless);

    /// <summary><c>before_action</c> with a synchronous callback.</summary>
    public ControllerCallbacks<TController> Before(
        string? name,
        Action<TController> callback,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        Before(name, Synchronous(callback), only, except, @if, unless);

    /// <summary><c>after_action name, only:, except:, if:, unless:</c></summary>
    public ControllerCallbacks<TController> After(
        string? name,
        Func<TController, ValueTask> callback,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        Append(name, CallbackKind.After, callback, only, except, @if, unless);

    /// <summary><c>after_action</c> with a synchronous callback.</summary>
    public ControllerCallbacks<TController> After(
        string? name,
        Action<TController> callback,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        After(name, Synchronous(callback), only, except, @if, unless);

    /// <summary><c>skip_before_action name, only:, except:, if:, unless:</c></summary>
    public ControllerCallbacks<TController> SkipBefore(
        string name,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        Skip(name, CallbackKind.Before, only, except, @if, unless);

    /// <summary><c>skip_after_action name, only:, except:, if:, unless:</c></summary>
    public ControllerCallbacks<TController> SkipAfter(
        string name,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? except = null,
        Func<TController, bool>? @if = null,
        Func<TController, bool>? unless = null) =>
        Skip(name, CallbackKind.After, only, except, @if, unless);

    /// <summary>
    /// <c>rescue_from TException { ... }</c>: an exception from a callback or the action is handed
    /// to the handler, on a fresh response. The last handler declared for a matching type wins.
    /// </summary>
    public ControllerCallbacks<TController> RescueFrom<TException>(Func<TController, TException, ValueTask> handler) where TException : Exception =>
        new(callbacks, rescues.Add(new Rescue(typeof(TException), (controller, error) => handler(controller, (TException)error))));

    /// <summary><c>rescue_from</c> with a synchronous handler.</summary>
    public ControllerCallbacks<TController> RescueFrom<TException>(Action<TController, TException> handler) where TException : Exception =>
        RescueFrom<TException>((controller, error) =>
        {
            handler(controller, error);
            return ValueTask.CompletedTask;
        });

    /// <summary>
    /// <c>process_action</c>: the before callbacks, the action (then <c>default_render</c> if it
    /// rendered nothing) and the after callbacks, with <c>rescue_from</c> around them.
    /// </summary>
    internal async ValueTask ProcessAsync(TController controller, Func<TController, ValueTask> action)
    {
        try
        {
            if (!await RunBeforeAsync(controller).ConfigureAwait(false))
            {
                return;
            }
            controller.BeginAction();
            await action(controller).ConfigureAwait(false);
            if (!controller.Performed)
            {
                controller.DefaultRender();
            }
            for (var i = callbacks.Length - 1; i >= 0; i--)
            {
                var callback = callbacks[i];
                if (callback.Kind == CallbackKind.After && Applies(callback, controller))
                {
                    await callback.Run(controller).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (HandlerFor(error) is { } rescue)
        {
            controller.ResetResponse();
            await rescue.Handle(controller, error).ConfigureAwait(false);
        }
    }

    // True when the action should run; false when a callback halted the chain.
    async ValueTask<bool> RunBeforeAsync(TController controller)
    {
        foreach (var callback in callbacks)
        {
            if (callback.Kind != CallbackKind.Before || !Applies(callback, controller))
            {
                continue;
            }
            await callback.Run(controller).ConfigureAwait(false);
            if (controller.Performed)
            {
                return false;
            }
        }
        return true;
    }

    Rescue? HandlerFor(Exception error)
    {
        for (var i = rescues.Length - 1; i >= 0; i--)
        {
            if (rescues[i].Type.IsInstanceOfType(error))
            {
                return rescues[i];
            }
        }
        return null;
    }

    static bool Applies(Callback callback, TController controller) =>
        callback.If.All(condition => condition(controller)) && !callback.Unless.Any(condition => condition(controller));

    ControllerCallbacks<TController> Append(
        string? name,
        CallbackKind kind,
        Func<TController, ValueTask> run,
        IReadOnlyCollection<string>? only,
        IReadOnlyCollection<string>? except,
        Func<TController, bool>? @if,
        Func<TController, bool>? unless)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (ifs, unlesses) = Conditions(only, except, @if, unless);
        var kept = name is null ? callbacks : callbacks.RemoveAll(callback => callback.Name == name && callback.Kind == kind);
        return new(kept.Add(new Callback(name, kind, run, ifs, unlesses)), rescues);
    }

    ControllerCallbacks<TController> Skip(
        string name,
        CallbackKind kind,
        IReadOnlyCollection<string>? only,
        IReadOnlyCollection<string>? except,
        Func<TController, bool>? @if,
        Func<TController, bool>? unless)
    {
        var index = callbacks.FindIndex(callback => callback.Name == name && callback.Kind == kind);
        if (index < 0)
        {
            throw new ArgumentException($"{(kind == CallbackKind.Before ? "Before" : "After")} process_action callback :{name} has not been defined");
        }
        var (ifs, unlesses) = Conditions(only, except, @if, unless);
        if (ifs.IsEmpty && unlesses.IsEmpty)
        {
            return new(callbacks.RemoveAt(index), rescues);
        }
        // The skip's if: becomes the callback's unless:, and its unless: the callback's if:.
        var callback = callbacks[index];
        var merged = callback with { If = callback.If.AddRange(unlesses), Unless = callback.Unless.AddRange(ifs) };
        return new(callbacks.SetItem(index, merged), rescues);
    }

    // _normalize_callback_options: only: is an if: on action_name, except: an unless:.
    static (ImmutableArray<Func<TController, bool>> If, ImmutableArray<Func<TController, bool>> Unless) Conditions(
        IReadOnlyCollection<string>? only,
        IReadOnlyCollection<string>? except,
        Func<TController, bool>? @if,
        Func<TController, bool>? unless)
    {
        var ifs = ImmutableArray.CreateBuilder<Func<TController, bool>>();
        var unlesses = ImmutableArray.CreateBuilder<Func<TController, bool>>();
        if (only is not null)
        {
            var actions = only.ToHashSet(StringComparer.Ordinal);
            ifs.Add(controller => actions.Contains(controller.ActionName));
        }
        if (except is not null)
        {
            var actions = except.ToHashSet(StringComparer.Ordinal);
            unlesses.Add(controller => actions.Contains(controller.ActionName));
        }
        if (@if is not null)
        {
            ifs.Add(@if);
        }
        if (unless is not null)
        {
            unlesses.Add(unless);
        }
        return (ifs.ToImmutable(), unlesses.ToImmutable());
    }

    static Func<TController, ValueTask> Synchronous(Action<TController> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return controller =>
        {
            callback(controller);
            return ValueTask.CompletedTask;
        };
    }

    internal sealed record Callback(
        string? Name,
        CallbackKind Kind,
        Func<TController, ValueTask> Run,
        ImmutableArray<Func<TController, bool>> If,
        ImmutableArray<Func<TController, bool>> Unless);

    internal sealed record Rescue(Type Type, Func<TController, Exception, ValueTask> Handle);
}

/// <summary>Where a controller's chain starts: <c>ControllerCallbacks.Empty&lt;MyController&gt;()</c>.</summary>
public static class ControllerCallbacks
{
    /// <summary>A chain with no callbacks.</summary>
    public static ControllerCallbacks<TController> Empty<TController>() where TController : Controller => new([], []);
}

enum CallbackKind
{
    Before,
    After,
}

static class ImmutableArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> array, Func<T, bool> predicate)
    {
        for (var i = 0; i < array.Length; i++)
        {
            if (predicate(array[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
