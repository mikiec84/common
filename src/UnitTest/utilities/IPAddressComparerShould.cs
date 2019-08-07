using gov.sandia.sld.common.utilities;
using System.Net;
using Xunit;

namespace UnitTest.utilities
{
    public class IPAddressComparerShould
    {
        [Fact]
        public void CompareAddressesProperly()
        {
            IPAddressComparer c = new IPAddressComparer();
            IPAddress a = IPAddress.Parse("1.2.3.4");
            IPAddress b = IPAddress.Parse("1.2.3.4");

            Assert.Equal(0, c.Compare(a, b));
            Assert.True(c.Equals(a, b));

            b = IPAddress.Parse("1.2.3.5");

            Assert.Equal(1, c.Compare(a, b));
            Assert.False(c.Equals(a, b));

            a = IPAddress.Parse("254.253.252.251");
            b = IPAddress.Parse("254.253.252.250");

            Assert.Equal(-1, c.Compare(a, b));
            Assert.False(c.Equals(a, b));

            a = IPAddress.Parse("254.254.254.254");
            b = IPAddress.Parse("254.254.254.254");

            Assert.Equal(0, c.Compare(a, b));
            Assert.True(c.Equals(a, b));
        }
    }
}
