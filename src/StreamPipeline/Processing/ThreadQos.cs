using System.Runtime.InteropServices;

namespace StreamPipeline.Processing;

/// <summary>
/// macOS: повишава QoS класа на текущата нишка до USER_INTERACTIVE, за да предпочете
/// планировчикът производителните ядра. Без това при ниско общо натоварване ОС може да
/// постави работниците на ефективните ядра (≈ 2–2,5 пъти по-бавни за FFT).
/// На други платформи е празна операция.
/// </summary>
public static partial class ThreadQos
{
    private const int QosClassUserInteractive = 0x21;

    [ThreadStatic] private static bool _applied;

    public static void EnsureInteractive()
    {
        if (_applied) return;
        _applied = true;
        if (OperatingSystem.IsMacOS())
            _ = pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
    }

    [LibraryImport("libSystem.dylib")]
    private static partial int pthread_set_qos_class_self_np(int qosClass, int relativePriority);
}
