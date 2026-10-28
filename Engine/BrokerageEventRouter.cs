using QuantConnect.Brokerages;
using QuantConnect.Interfaces;

namespace QuantConnect.Lean.Engine;

/// <summary>Routes brokerage messages to LEAN's handler and locked algorithm callbacks.</summary>
internal static class BrokerageEventRouter
{
    public static void Subscribe(IBrokerage brokerage, IAlgorithm algorithm)
    {
        brokerage.Message += (_, message) =>
        {
            algorithm.BrokerageMessageHandler.HandleMessage(message);

            if (!algorithm.GetLocked()) return;

            algorithm.OnBrokerageMessage(message);
            switch (message.Type)
            {
                case BrokerageMessageType.Disconnect:
                    algorithm.OnBrokerageDisconnect();
                    break;
                case BrokerageMessageType.Reconnect:
                    algorithm.OnBrokerageReconnect();
                    break;
            }
        };
    }
}
