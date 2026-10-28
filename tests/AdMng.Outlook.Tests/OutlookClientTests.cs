using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook.Tests;

public class OutlookClientTests
{
    [Fact]
    public void Availability_IsNotSupportedOffWindows_AndAProgIdLookupOnWindows()
    {
        var client = new OutlookClient();

        // On Windows only the registry is read (the ProgID): Outlook is neither started nor attached to.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(client.Availability, new[] { OutlookAvailability.Installed, OutlookAvailability.NotInstalled });
        }
        else
        {
            Assert.Equal(OutlookAvailability.NotSupported, client.Availability);
        }
    }
}
