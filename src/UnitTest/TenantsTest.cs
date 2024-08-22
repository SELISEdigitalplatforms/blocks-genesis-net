using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using MongoDB.Driver;
using Xunit;
using System.Linq.Expressions;

namespace Blocks.Genesis.Tests
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
