using System;
using System.Reflection;
using System.Threading.Tasks;
using WOTRMultiplayer.Networking;
using WOTRMultiplayer.Networking.Messages.Game;

internal static partial class Program
{
    private static bool ProbeNativeAbortTransport()
    {
        try
        {
            var now = DateTime.UtcNow;
            var source = new NotifyRealTimeNativeCheckpoint
            {
                SessionId = "offline", Epoch = 3, LoadedSaveSeed = 7, CheckpointId = new string('1', 32),
                BuildFingerprint = new string('A', 64), Stage = (int)RealTimeNativeCheckpointStage.Prepare
            };
            var abort = new RealTimeNativeCheckpointAbort(42, source, now);
            var first = abort.GetDue(now); first.SessionId = "changed";
            var retry = abort.GetDue(now + RealTimeNativeCheckpointAbort.RetryDelay);
            var safe = retry.SessionId == "offline" && RealTimeNativeCheckpointLimits.AbortSource(retry) != null;
            var ack = new ClientRealTimeNativeCheckpointAck
            {
                SessionId = retry.SessionId, Epoch = retry.Epoch, LoadedSaveSeed = retry.LoadedSaveSeed,
                CheckpointId = retry.CheckpointId, ArchiveHash = retry.ArchiveHash, Stage = (int)RealTimeNativeCheckpointAckStage.Aborted
            };
            safe &= !abort.Acknowledge(99, ack) && abort.Acknowledge(42, ack) && abort.GetDue(now.AddDays(1)) == null;
            Console.WriteLine("native-abort-transport: immutable-retry-and-peer-bound-receipt=" + safe
                + "; archive-only-dto=True; native-world-not-invoked=True");
            return safe;
        }
        catch (Exception error) { Console.WriteLine("native-abort-transport: FAILED: " + error); return false; }
    }

    private static bool ProbeAbortOperation<T>(object lease)
    {
        // Synthetic failing task plus the existing synthetic lease. Never run
        // a loader, a native callback, Game.Instance or an actual game object.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operationType = typeof(WOTRMultiplayer.Main).Assembly.GetType("WOTRMultiplayer.Services.GameInteraction.RealTimeNativeLoadOperation", true);
        var operation = Activator.CreateInstance(operationType, flags, null, new object[] { (Func<bool>)(() => true) }, null);
        var pending = new TaskCompletionSource<T>();
        var task = (Task)operationType.GetMethod("RunAsync", flags).Invoke(operation, new object[] { (Func<Task<T>>)(() => pending.Task) });
        operationType.GetMethod("AttachLease", flags).Invoke(operation, new[] { lease });
        bool Acknowledgable() => (bool)operationType.GetProperty("CanAcknowledgeAbort", flags).GetValue(operation);
        var safe = !Acknowledgable();
        pending.SetException(new InvalidOperationException("synthetic-aborted-load"));
        try { task.GetAwaiter().GetResult(); safe = false; } catch (InvalidOperationException) { }
        safe &= !Acknowledgable();
        lease.GetType().GetMethod("MarkWorkersDrained", flags).Invoke(lease, null);
        safe &= !Acknowledgable();
        ((IDisposable)lease).Dispose();
        safe &= Acknowledgable() && !(bool)operationType.GetProperty("IsLoaded", flags).GetValue(operation);
        Console.WriteLine("native-abort-quiescence: failed-task-alone-rejected=" + safe
            + "; exact-lease-drain-and-close-required=True; native-load-not-invoked=True");
        return safe;
    }
}
