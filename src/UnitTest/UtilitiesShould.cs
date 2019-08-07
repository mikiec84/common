using Xunit;

namespace UnitTest
{
    public class UtilitiesShould
    {
        [Fact]
        public void IsIPShould()
        {
            string ipTrue = "192.168.0.1";
            string ipFalse = "Cat";
            string ipFalse2 = "123423.3243.324.23";

            bool isValidIP = gov.sandia.sld.common.utilities.Extensions.IsIPAddress(ipTrue);
            bool isNotValidIP = gov.sandia.sld.common.utilities.Extensions.IsIPAddress(ipFalse);
            bool isNotValidIP2 = gov.sandia.sld.common.utilities.Extensions.IsIPAddress(ipFalse2);

            Assert.True(isValidIP);
            Assert.False(isNotValidIP);
            Assert.False(isNotValidIP2);
        }
    }
}
