using Moq;
using Blocks.Genesis;
namespace UnitTest.Tenant
{
    public class TenantsTests
    {
        private readonly Mock<IBlocksSecret> _blocksSecretMock;

        public TenantsTests()
        {
            _blocksSecretMock = new Mock<IBlocksSecret>();
            _blocksSecretMock.SetupGet(x => x.DatabaseConnectionString).Returns("mongodb://localhost:27017");
        }

        [Fact]
        public void TenantsConstructor_ShouldSetupCorrectly()
        {
            // Acr
            var tenants = new Tenants(_blocksSecretMock.Object);

            // Assert
            Assert.NotNull(tenants);
        }

    }
}
