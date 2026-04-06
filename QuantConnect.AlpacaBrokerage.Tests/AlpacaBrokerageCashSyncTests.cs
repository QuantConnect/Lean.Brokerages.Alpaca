using Moq;
using System.Threading;
using Alpaca.Markets;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public class AlpacaBrokerageCashSyncTests
    {
        [Test]
        public void GetCashBalance_UsesCashNotBuyingPower()
        {
            var mockAccount = new Mock<IAccount>();
            mockAccount.Setup(a => a.NonMarginableBuyingPower).Returns(346.58m);
            mockAccount.Setup(a => a.TradableCash).Returns(46300.11m);
            mockAccount.Setup(a => a.Currency).Returns("USD");

            var mockTradingClient = new Mock<IAlpacaTradingClient>();
            mockTradingClient
                .Setup(c => c.GetAccountAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockAccount.Object);

            var brokerage = new AlpacaBrokerage();
            typeof(AlpacaBrokerage)
                .GetField("_tradingClient", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(brokerage, mockTradingClient.Object);

            var result = brokerage.GetCashBalance();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(346.58m, result[0].Amount);
            Assert.AreEqual("USD", result[0].Currency);
        }
    }
}