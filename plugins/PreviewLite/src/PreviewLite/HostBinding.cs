using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace PreviewLite;

internal sealed record HostBinding(MethodInfo Loop, MethodInfo Edit, MethodInfo Draw, Func<object, bool> IsPlaying)
{
    internal const string SupportedVersion = "4.56.1.1";
    internal const string LoopHash = "C71AFC17AB4E6B5685F7ADC2A575D664189A8A15432FECC6CB6CB64DB4CDEB35";
    internal const string EditHash = "28666270AD78B205FF94B0B391981E99C29279845DE5078630DF6F97778EA876";
    internal const string DrawHash = "8F4AEBB53225B35651D883B537FC5CF8D68EBFCD8FC8235DF32C9401EC38F5DC";
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static HostBinding Resolve(Assembly assembly)
    {
        string? version = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        if (version != SupportedVersion) throw new NotSupportedException($"対象外のYMM4です：{version}（対応：{SupportedVersion}）");
        var player = assembly.GetType("YukkuriMovieMaker.Player.TimelineVideoPlayer", true)!;
        var loop = player.GetMethod("<BeginVideoTask>b__152_0", All)!;
        if (loop is null || Convert.ToHexString(SHA256.HashData(loop.GetMethodBody()!.GetILAsByteArray()!)) != LoopHash)
            throw new NotSupportedException("プレビュー内部処理が確認済みの版と異なるため、変更を停止しました。");
        var edit = player.GetMethod("Edit", All, [])!;
        var draw = player.GetMethod("Draw", All, [])!;
        if (Hash(edit) != EditHash || Hash(draw) != DrawHash) throw new NotSupportedException("Preview method code mismatch");
        var callers = player.GetMethods(All | BindingFlags.DeclaredOnly)
            .Where(m => m.GetMethodBody() is not null && PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(edit))).ToArray();
        if (callers.Length != 1 || callers[0] != loop) throw new NotSupportedException("Unexpected preview Edit caller");
        return Create(loop, edit, draw, player.BaseType!.GetProperty("IsPlaying", All)!.GetMethod!);
    }

    private static string Hash(MethodInfo method) => Convert.ToHexString(SHA256.HashData(method.GetMethodBody()!.GetILAsByteArray()!));

    internal static HostBinding Create(MethodInfo loop, MethodInfo edit, MethodInfo draw, MethodInfo playing)
    {
        if (new[] { loop, edit, draw, playing }.Any(x => x is null)) throw new MissingMethodException("Preview API incomplete");
        return new(loop, edit, draw, Make<Func<object, bool>>(playing, [typeof(object)]));
    }

    // Call unmodified host methods with the exact parameter stack; no public/internal host types leak into our plugin.
    private static T Make<T>(MethodInfo method, Type[] args) where T : Delegate
    {
        var dm = new DynamicMethod("PreviewLite_" + method.Name, method.ReturnType, args, typeof(HostBinding).Module, true);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, method.DeclaringType!);
        for (short i = 1; i < args.Length; i++) il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Callvirt, method);
        il.Emit(OpCodes.Ret);
        return dm.CreateDelegate<T>();
    }

    internal static void CheckConflicts(HostBinding binding)
    {
        // Unknown patches can change the same scheduling contract. Fail closed instead of composing them.
        foreach (var method in new[] { binding.Loop, binding.Edit, binding.Draw }.Concat(typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Delay")))
        {
            var owners = Harmony.GetPatchInfo(method)?.Owners.Where(x => x != PreviewRuntime.Owner).ToArray();
            if (owners is { Length: > 0 }) throw new InvalidOperationException("別のプレビュー調整と競合しています。先に無効化してください：" + string.Join(", ", owners));
        }
    }
}
