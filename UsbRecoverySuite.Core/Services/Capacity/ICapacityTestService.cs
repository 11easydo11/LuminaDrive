using UsbRecoverySuite.Core.Models;

namespace UsbRecoverySuite.Core.Services.Capacity;

public interface ICapacityTestService
{
    Task<CapacityTestResult> RunCapacityTestAsync(
        UsbDeviceInfo device,
        long testSizeBytes,
        IProgress<CapacityProgressReport> progress,
        CancellationToken cancellationToken);
}
