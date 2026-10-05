namespace UsbRecoverySuite.Core.Models;

public enum UsbStorageDeviceType
{
    UsbFlashDrive,
    ExternalUsbDisk,
    OtherUsbStorage,
    InternalDisk
}

public enum HealthStatus
{
    Ok,
    Warning,
    Error,
    Critical,
    Unknown
}

public enum PartitionTableType
{
    Unknown,
    MBR,
    GPT,
    RAW
}

public enum DiagnosticCategory
{
    Safety,
    Partition,
    Filesystem,
    StorageDevice,
    Capacity
}

public enum RepairActionType
{
    None,
    FixAll,
    CleanAndPartitionAll,
    ClearReadOnly,
    CheckAndRepairFilesystem,
    FormatVolume,
    ReinitializeGpt,
    ReinitializeMbr,
    CreatePartition,
    DeletePartition,
    AssignDriveLetter,
    RemoveDriveLetter
}

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error
}
