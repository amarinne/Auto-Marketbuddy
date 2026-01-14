using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;

namespace AutoMarketbuddy.Services
{
    public class AllaganMarketClient
    {
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICallGateSubscriber<uint, uint, bool, uint> getRecommendedPriceSubscriber;
        private readonly ICallGateSubscriber<int, (uint, uint, uint, bool)> getSaleItemSubscriber;

        public const string GetRecommendedPriceLabel = "AllaganMarket.GetRecommendedPrice";
        public const string GetSaleItemLabel = "AllaganMarket.GetSaleItem";

        public AllaganMarketClient(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
            this.getRecommendedPriceSubscriber = this.pluginInterface.GetIpcSubscriber<uint, uint, bool, uint>(GetRecommendedPriceLabel);
            this.getSaleItemSubscriber = this.pluginInterface.GetIpcSubscriber<int, (uint, uint, uint, bool)>(GetSaleItemLabel);
        }

        public (uint ItemId, uint WorldId, uint Price, bool IsHq)? GetSaleItem(int index)
        {
             try
             {
                 var result = this.getSaleItemSubscriber.InvokeFunc(index);
                 if (result.Item1 == 0) return null; // Assume 0 itemID is invalid
                 return result;
             }
             catch
             {
                 return null;
             }
        }

        public uint GetRecommendedPrice(uint worldId, uint itemId, bool isHq)
        {
            try
            {
                return this.getRecommendedPriceSubscriber.InvokeFunc(worldId, itemId, isHq);
            }
            catch (Exception)
            {
                // Plugin might not be loaded or logic error
                // invoke failed
                return 0;
            }
        }
    }
}
