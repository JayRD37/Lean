using System;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp;

/// <summary>
/// Estimates Alpaca's pass-through US equity CAT, FINRA TAF, and SEC fees for LEAN fills.
/// Rates match Alpaca's fee schedule revised September 17, 2026; actual charges are rounded
/// after daily account-level aggregation, which LEAN's per-order fee interface cannot reproduce.
/// <see href="https://files.alpaca.markets/disclosures/BrokFeeSched.pdf">Alpaca fee schedule</see>
/// </summary>
public sealed class AlpacaEquityRegulatoryFeeModel : FeeModel
{
    public const decimal SecTransactionFeePerDollar = 0.0000206m;
    public const decimal FinraTradingActivityFeePerShare = 0.000195m;
    public const decimal FinraTradingActivityFeeCap = 9.79m;
    public const decimal CatFeePerExecutedShare = 0.000003m;

    private readonly IFeeModel _fallback = new AlpacaFeeModel();

    public override OrderFee GetOrderFee(OrderFeeParameters parameters)
    {
        var security = parameters.Security;
        if (security.Type != SecurityType.Equity || security.Symbol.ID.Market != Market.USA)
        {
            return _fallback.GetOrderFee(parameters);
        }

        var order = parameters.Order;
        var quantity = order.AbsoluteQuantity;
        if (quantity == 0) return OrderFee.Zero;

        var catFee = quantity * CatFeePerExecutedShare;
        var transactionFees = catFee;
        if (order.Direction == OrderDirection.Sell)
        {
            var tradeValue = Math.Abs(order.GetValue(security));
            var tradingActivityFee = Math.Min(quantity, FinraTradingActivityFeeCap / FinraTradingActivityFeePerShare)
                * FinraTradingActivityFeePerShare;
            transactionFees += tradeValue * SecTransactionFeePerDollar + tradingActivityFee;
        }

        return new OrderFee(new CashAmount(transactionFees, Currencies.USD));
    }
}
