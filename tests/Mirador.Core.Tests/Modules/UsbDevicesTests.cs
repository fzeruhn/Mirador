using Mirador.Core.Modules;

namespace Mirador.Core.Tests.Modules;

public class UsbDevicesTests
{
    [Fact]
    public void Device_ids_give_vendor_and_product()
    {
        string?[] ids =
        [
            @"USB\VID_045E&PID_028E\5&2B3C1F0&0&1",
            @"USB\VID_1D6B&PID_0102&MI_00\6&1A2B3C4D&0&0000",
            @"USB\VID_1D6B&PID_0102&MI_01\6&1A2B3C4D&0&0001", // the same device's second interface
            @"usb\vid_28de&pid_2300\7&abc&0&2",
            @"USB\ROOT_HUB30\4&1234&0&0",
            @"USB\VID_ZZZZ&PID_0001\1",
            @"HID\VID_046D&PID_C52B&MI_00\7&1",
            null,
            "",
        ];

        var present = WmiUsbDevices.Parse(ids);

        Assert.Equal(
            new[] { new UsbId(0x045E, 0x028E), new UsbId(0x1D6B, 0x0102), new UsbId(0x28DE, 0x2300) },
            present.OrderBy(id => id.Vid).ThenBy(id => id.Pid));
    }
}
