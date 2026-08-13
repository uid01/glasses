using HidSharp;

const int VendorId = 0x3318;
const int ProductId = 0x043E;

var all = DeviceList.Local.GetHidDevices().ToList();
Console.WriteLine($"Total HID devices visible to HidSharp: {all.Count}");

var matchingVendor = all.Where(d => d.VendorID == VendorId).ToList();
Console.WriteLine($"Devices with VendorID=0x{VendorId:X4}: {matchingVendor.Count}");
foreach (var d in matchingVendor)
{
    Console.WriteLine($"  VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4} Path={d.DevicePath}");
}

if (matchingVendor.Count == 0)
{
    Console.WriteLine();
    Console.WriteLine("No devices with that VendorID at all -- listing first 15 of all devices for comparison:");
    foreach (var d in all.Take(15))
    {
        Console.WriteLine($"  VID=0x{d.VendorID:X4} PID=0x{d.ProductID:X4} Path={d.DevicePath}");
    }
}
