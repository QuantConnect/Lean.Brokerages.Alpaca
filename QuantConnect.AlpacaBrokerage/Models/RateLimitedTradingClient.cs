/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using Alpaca.Markets;
using System.Threading;
using QuantConnect.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Alpaca.Models;

/// <summary>
/// Provides a rate-limited wrapper around an <see cref="IAlpacaTradingClient"/>.
/// <para>
/// This decorator ensures that API calls respect Alpaca’s rate limits by blocking
/// requests when the remaining quota has been exhausted until the reset time is reached,
/// or until cancellation is requested.
/// </para>
/// </summary>
public class RateLimitedTradingClient : IDisposable
{
    /// <summary>
    /// The underlying Alpaca trading client that handles actual API calls.
    /// </summary>
    private readonly IAlpacaTradingClient _inner;

    /// <summary>
    /// Global cancellation token source used to cancel all pending rate-limit waits.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitedTradingClient"/> class,
    /// wrapping an <see cref="IAlpacaTradingClient"/> with built-in rate limit enforcement.
    /// </summary>
    /// <param name="environment">Target environment for new object.</param>
    /// <param name="securityKey">Alpaca API security key.</param>
    public RateLimitedTradingClient(IEnvironment environment, SecurityKey securityKey)
    {
        _inner = EnvironmentExtensions.GetAlpacaTradingClient(environment, securityKey);
    }

    /// <summary>
    /// Disposes the current instance, cancelling any pending rate-limit waits and releasing all managed resources.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _inner.Dispose();
    }

    /// <summary>
    /// Gets list of available assets from Alpaca REST API endpoint.
    /// </summary>
    /// <param name="request">Asset list request parameters.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only list of asset information objects.</returns>
    public Task<IReadOnlyList<IAsset>> ListAssetsAsync(AssetsRequest request, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.ListAssetsAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets list of available orders from Alpaca REST API endpoint.
    /// </summary>
    /// <param name="request">List orders request parameters.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only list of order information objects.</returns>
    public Task<IReadOnlyList<IOrder>> ListOrdersAsync(ListOrdersRequest request, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.ListOrdersAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets list of available positions from Alpaca REST API endpoint.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only list of position information objects.</returns>
    public Task<IReadOnlyList<IPosition>> ListPositionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.ListPositionsAsync(cancellationToken);
    }

    /// <summary>
    /// Gets account information from Alpaca REST API endpoint.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only account information.</returns>
    public Task<IAccount> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.GetAccountAsync(cancellationToken);
    }

    /// <summary>
    /// Creates new order for execution using Alpaca REST API endpoint.
    /// </summary>
    /// <param name="orderBase">New order placement request parameters.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only order information object for newly created order.</returns>
    public Task<IOrder> PostOrderAsync(OrderBase orderBase, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.PostOrderAsync(orderBase, cancellationToken);
    }

    /// <summary>
    /// Updates existing order using Alpaca REST API endpoint.
    /// </summary>
    /// <param name="request">Patch order request parameters.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only order information object for updated order.</returns>
    public Task<IOrder> PatchOrderAsync(ChangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.PatchOrderAsync(request, cancellationToken);
    }

    /// <summary>
    /// Cancels order on server by server order ID using Alpaca REST API endpoint.
    /// </summary>
    /// <param name="orderId">Server order ID for cancelling.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns><c>True</c> if order cancellation was accepted.</returns>
    public Task<Boolean> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.CancelOrderAsync(orderId, cancellationToken);
    }

    /// <summary>
    /// Gets list of active option contracts from Alpaca REST API endpoint. By default, only active contracts that expire before the upcoming weekend are returned.
    /// </summary>
    /// <param name="request">Option contracts request parameters.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Read-only list of corporate action information objects.</returns>
    public Task<IPage<IOptionContract>> ListOptionContractsAsync(OptionContractsRequest request, CancellationToken cancellationToken = default)
    {
        EnsureRateLimit(_inner);
        return _inner.ListOptionContractsAsync(request, cancellationToken);
    }

    /// <summary>
    /// Ensures that the request rate limit has not been exceeded.
    /// <para>
    /// If the <see cref="IRateLimitProvider"/> reports that no requests remain in the current
    /// rate limit window, this method will block the calling thread until the reset time is reached
    /// or until the global cancellation token for this <see cref="RateLimitedTradingClient"/> is triggered.
    /// </para>
    /// <para>
    /// Logging will provide details when waiting starts, when it is cancelled,
    /// and when waiting completes.
    /// </para>
    /// </summary>
    /// <param name="rateLimitProvider">
    /// The <see cref="IRateLimitProvider"/> used to query current rate limit values.
    /// Typically this will be the wrapped <see cref="IAlpacaTradingClient"/>.
    /// </param>
    /// <exception cref="OperationCanceledException">
    /// Thrown if the global cancellation token is signaled during the wait.
    /// </exception>
    private void EnsureRateLimit(IRateLimitProvider rateLimitProvider)
    {
        var limits = rateLimitProvider.GetRateLimitValues();
        if (limits.Remaining <= 0)
        {
            var delay = limits.ResetTimeUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                Log.Trace($"{nameof(RateLimitedTradingClient)}.{nameof(EnsureRateLimit)}: Rate limit reached. Waiting {delay.TotalSeconds:F1}s until {limits.ResetTimeUtc:u}...");
                if (_cts.Token.WaitHandle.WaitOne(delay))
                {
                    Log.Trace($"{nameof(RateLimitedTradingClient)}.{nameof(EnsureRateLimit)}: Wait cancelled.");
                    _cts.Token.ThrowIfCancellationRequested();
                }
                Log.Trace($"{nameof(RateLimitedTradingClient)}.{nameof(EnsureRateLimit)}: Wait finished, resuming execution.");
            }
        }
    }
}
