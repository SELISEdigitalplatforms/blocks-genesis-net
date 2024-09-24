using Moq;
using Blocks.Genesis;
namespace UnitTest.Tenant
{
    public class TenantsTests
    {
        private readonly Mock<IBlocksSecret> _blocksSecretMock;
        private readonly Mock<ICacheClient> _cacheClientMock;

        public TenantsTests()
        {
            _blocksSecretMock = new Mock<IBlocksSecret>();
            _cacheClientMock = new Mock<ICacheClient>();
            _blocksSecretMock.SetupGet(x => x.DatabaseConnectionString).Returns("mongodb://localhost:27017");
            _blocksSecretMock.SetupGet(x => x.RooDatabaseName).Returns("Blocks");
        }

        //[Fact]
        //public void TenantsConstructor_ShouldSetupCorrectly()
        //{
        //    // Acr
        //    var tenants = new Tenants(_blocksSecretMock.Object, _cacheClientMock.Object);

        //    // Assert
        //    Assert.NotNull(tenants);
        //}

    }
}
