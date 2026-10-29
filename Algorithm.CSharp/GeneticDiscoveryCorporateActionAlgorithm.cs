using System;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp;

/// <summary>
/// Focused LEAN integration check for raw-mode split holdings and dividend events.
/// </summary>
public sealed class GeneticDiscoveryCorporateActionAlgorithm : QCAlgorithm
{
    private Symbol _aapl;
    private bool _entrySubmitted;
    private bool _splitObserved;
    private bool _dividendObserved;

    public override void Initialize()
    {
        SetStartDate(2020, 7, 1);
        SetEndDate(2020, 9, 10);
        SetCash(100_000);
        _aapl = AddEquity("AAPL", Resolution.Daily, dataNormalizationMode: DataNormalizationMode.Raw).Symbol;
    }

    public override void OnData(Slice slice)
    {
        if (!_entrySubmitted && slice.Bars.ContainsKey(_aapl))
        {
            MarketOrder(_aapl, 1);
            _entrySubmitted = true;
        }

        if (slice.Dividends.TryGetValue(_aapl, out var dividend))
        {
            if (Portfolio[_aapl].Quantity <= 0 || Portfolio[_aapl].TotalDividends <= 0)
            {
                throw new InvalidOperationException("The raw-mode dividend event did not credit the held AAPL position.");
            }

            _dividendObserved = true;
            Debug($"Corporate action check: dividend {dividend.Symbol} distribution={dividend.Distribution} credited={Portfolio[_aapl].TotalDividends}");
        }

        if (slice.Splits.TryGetValue(_aapl, out var split) && split.Type == SplitType.SplitOccurred)
        {
            var quantity = Portfolio[_aapl].Quantity;
            if (quantity != 4)
            {
                throw new InvalidOperationException($"Expected one AAPL share to become four after the raw-mode split; found {quantity}.");
            }

            _splitObserved = true;
            Debug($"Corporate action check: split {split.Symbol} factor={split.SplitFactor} holdingQuantity={quantity}");
        }
    }

    public override void OnEndOfAlgorithm()
    {
        if (!_entrySubmitted) throw new InvalidOperationException("No AAPL position was opened before the split test.");
        if (!_splitObserved) throw new InvalidOperationException("The AAPL split event was not observed.");
        if (!_dividendObserved) throw new InvalidOperationException("The AAPL dividend event was not observed.");
    }
}


