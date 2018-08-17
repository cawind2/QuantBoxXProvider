﻿using System;
using System.Collections.Generic;
using System.Reflection;
using QuantBox.XApi;
using SmartQuant;
using InstrumentType = SmartQuant.InstrumentType;
using PutCall = SmartQuant.PutCall;

namespace QuantBox
{
    internal class Convertor
    {
        private static readonly List<FieldInfo> AccountFields = new List<FieldInfo>();
        private static readonly DepthMarketDataField EmptyMarketData = new DepthMarketDataField();

        private readonly XProvider _provider;
        private readonly Dictionary<string, AccountPosition> _positions = new Dictionary<string, AccountPosition>();
        private readonly IdArray<DepthMarketDataField> _marketData = new IdArray<DepthMarketDataField>();
        private readonly IdArray<bool> _instOpenFlag = new IdArray<bool>(100);
        private readonly IdArray<bool> _instCloseFlag = new IdArray<bool>(100);
        private readonly IdArray<bool> _czceInstFlag = new IdArray<bool>(100);
        private readonly IDictionary<string, Instrument> _instruments = new Dictionary<string, Instrument>();
        private readonly IdArray<TradingTimeRange> _timeRanges = new IdArray<TradingTimeRange>();

        static Convertor()
        {
            foreach (var field in typeof(AccountField).GetFields()) {
                AccountFields.Add(field);
            }
            EmptyMarketData.OpenInterest = 0;
            EmptyMarketData.Turnover = 0;
            EmptyMarketData.Volume = 0;
        }

        public Convertor(XProvider provider)
        {
            _provider = provider;
            Init();
        }

        public void Init()
        {
            _instOpenFlag.Clear();
            _instCloseFlag.Clear();
            _czceInstFlag.Clear();
            _instruments.Clear();
        }

        internal void InitInstrument(Instrument inst)
        {
            if (inst.GetExchange(_provider.GetAltId()).ToUpper() == "CZCE") {
                _czceInstFlag[inst.Id] = true;
            }
            if (_provider.EnableMarketLog) {
                _provider.Logger.Debug($"{inst.Symbol} init.");
            }
            var key = inst.GetSymbol(_provider.GetAltId());
            if (inst.Type == InstrumentType.Stock || inst.Type == InstrumentType.Index) {
                key = key + "." + inst.GetExchange(_provider.GetAltId());
            }
            _instruments[key] = inst;
            _marketData[inst.Id] = EmptyMarketData;
            _instCloseFlag[inst.Id] = false;
            _instOpenFlag[inst.Id] = false;
            if (_provider.DiscardOutOfTimeRange) {
                _timeRanges[inst.Id] = TradingCalendar.Instance.GetTimeRange(inst, DateTime.Today);
            }
            else {
                _timeRanges[inst.Id] = TradingTimeRange.Fulltime;
            }
        }

        public static HedgeFlagType GetHedgeFlag(Order order, HedgeFlagType defaultValue)
        {
            var flag = order.GetHedgeFlag();
            if (flag == HedgeFlagType.Undefined) {
                flag = defaultValue;
            }
            return flag;
        }

        public static XApi.OrderSide GetSide(Order order)
        {
            var side = order.GetSide();
            if (side == XApi.OrderSide.Undefined) {
                return (XApi.OrderSide)order.Side;
            }
            return side;
        }

        public Instrument GetInstument(InstrumentField field)
        {
            var inst = new Instrument((InstrumentType)field.Type, field.Symbol);
            inst.AltId.Add(new AltId(_provider.Id, field.InstrumentID, field.ExchangeID));
            inst.PutCall = (PutCall)field.OptionsType;
            inst.Strike = field.StrikePrice;
            inst.Exchange = field.ExchangeID;
            inst.CurrencyId = CurrencyId.CNY;
            inst.TickSize = field.PriceTick;
            inst.Factor = field.VolumeMultiple;
            inst.PriceFormat = "F" + QBHelper.GetPrecision(field.PriceTick);
            inst.Maturity = field.ExpireDate();
            if (!string.IsNullOrEmpty(field.UnderlyingInstrID)
                && field.UnderlyingInstrID != field.ProductID
                && !field.InstrumentID.EndsWith("efp")) {
                var underlying = _provider.InstrumentManager.Get(field.UnderlyingInstrID);
                if (underlying == null) {
                    //_provider.Logger.Warn($"没有找到合约标的物{field.UnderlyingInstrID},请先导入合约标的物");
                }
                else {
                    OpenQuant.Helper.AddLeg(inst, new Leg(underlying));
                }
            }
            return inst;
        }

        public void ProcessAccount(AccountField account)
        {
            var data = new AccountData(DateTime.Now, AccountDataType.AccountValue, account.AccountID, _provider.Id, _provider.Id);
            foreach (var field in AccountFields) {
                data.Fields.Add(field.Name, field.GetValue(account));
            }
            data.Fields.Add(QBHelper.UserDataName, account);
            _provider.ProcessAccount(data);
        }

        public void ProcessPosition(PositionField position)
        {
            if (position == null) {
                return;
            }
            if (!_positions.TryGetValue(position.InstrumentID, out var item)) {
                item = new AccountPosition();
                _positions.Add(position.InstrumentID, item);
            }
            item.AddPosition(position);

            var data = new AccountData(DateTime.Now, AccountDataType.Position, position.AccountID, _provider.Id, _provider.Id);
            data.Fields.Add(AccountDataField.SYMBOL, item.InstrumentId);
            data.Fields.Add(AccountDataField.EXCHANGE, item.ExchangeId);
            data.Fields.Add(AccountDataField.QTY, item.Qty);
            data.Fields.Add(AccountDataField.LONG_QTY, item.LongQty);
            data.Fields.Add(AccountDataField.SHORT_QTY, item.ShortQty);
            data.Fields.Add(QBHelper.UserDataName, item);
            _provider.ProcessAccount(data);
        }

        public void ProcessMarketData(DepthMarketDataField field)
        {
            if (field == null) {
                return;
            }

            if (_provider.EnableMarketLog) {
                //_provider.Logger.Debug($"{field.InstrumentID},{field.UpdateTime},{field.Bids[0].Price},{field.Bids[0].Size},{field.Asks[0].Price},{field.Asks[0].Size},{field.LastPrice},{field.Volume}.");
                _provider.Logger.Debug($"{field.InstrumentID},{field.UpdateTime},{field.LastPrice},{field.Volume}.");
            }

            _instruments.TryGetValue(field.InstrumentID, out var inst);
            if (inst == null) {
                if (_provider.EnableMarketLog) {
                    _provider.Logger.Debug($"unsubscribed tick: {field.InstrumentID}.");
                }
                return;
            }

            var instId = inst.Id;
            var localTime = DateTime.Now;
            var exchangeTime = field.ExchangeDateTime();
            if (exchangeTime.Year == DateTime.MaxValue.Year) {
                if (_provider.EnableMarketLog) {
                    _provider.Logger.Debug($"empty trading time, {field.InstrumentID}");
                }
                exchangeTime = localTime;
            }
            else {
                if (_provider.NightTradingTimeCorrection) {
                    exchangeTime = Helper.CorrectionActionDay(localTime, exchangeTime);
                }
            }

            var time = exchangeTime.TimeOfDay;
            if (_provider.MaxTimeDiffExchangeLocal > 0) {
                var diff = Math.Abs((localTime.TimeOfDay - time).TotalMinutes);
                if (diff > _provider.MaxTimeDiffExchangeLocal) {
                    if (_provider.EnableMarketLog) {
                        _provider.Logger.Debug($"time diff ={diff},{field.InstrumentID}");
                    }
                    return;
                }
            }

            var range = _timeRanges[instId];
            if (!_instOpenFlag[instId]) {
                if (range.InRange(time)) {
                    if (_instCloseFlag[instId] && range.IsClose(time)) {
                        if (_provider.EnableMarketLog) {
                            _provider.Logger.Debug($"already closed, {field.InstrumentID}.");
                        }
                        return;
                    }
                    inst.SetMarketData(field);
                    _instOpenFlag[instId] = true;
                    _instCloseFlag[instId] = false;
                    if (_provider.EnableMarketLog) {
                        _provider.Logger.Debug($"market open, {field.InstrumentID}.");
                    }
                }
                else {
                    if (_provider.EnableMarketLog) {
                        _provider.Logger.Debug($"market no open, {field.InstrumentID}.");
                    }
                    return;
                }
            }

            var last = _marketData[instId];
            if (field.ClosePrice > 0 && range.IsClose(time)) {
                inst.SetMarketData(field);
                _instCloseFlag[instId] = true;
                _instOpenFlag[instId] = false;
                _marketData[instId] = EmptyMarketData;
                if (_provider.EnableMarketLog) {
                    _provider.Logger.Debug($"market close, {field.InstrumentID}.");
                }
            }
            else {
                if (_czceInstFlag[inst.Id]) {
                    field.ClosePrice = 0;
                }
                _marketData[instId] = field;
            }

            if (field.Asks?.Length > 0 && field.Asks[0].Size > 0) {
                var ask = OpenQuant.Helper.NewTick<Ask>(localTime, exchangeTime, _provider.Id, instId, field.Asks[0].Price, field.Asks[0].Size);
                _provider.ProcessMarketData(ask);
            }
            if (field.Bids?.Length > 0 && field.Bids[0].Size > 0) {
                var bid = OpenQuant.Helper.NewTick<Bid>(localTime, exchangeTime, _provider.Id, instId, field.Bids[0].Price, field.Bids[0].Size);
                _provider.ProcessMarketData(bid);
            }

            if (!(field.LastPrice > double.Epsilon)) {
                if (_provider.EnableMarketLog) {
                    _provider.Logger.Debug($"empty price, {field.InstrumentID}.");
                }
                return;
            }

            var size = _provider.VolumeIsAccumulated ? field.Volume - last.Volume : field.Volume;
            if (_provider.DiscardEmptyTrade
                && _provider.VolumeIsAccumulated
                && Math.Abs(size) < double.Epsilon
                && _instOpenFlag[instId]) {
                if (_provider.EnableMarketLog) {
                    _provider.Logger.Debug($"empty trade, {field.InstrumentID}.");
                }
                return;
            }
            double openInterest, turnover;
            var trade = OpenQuant.Helper.NewTick<Trade>(localTime, exchangeTime, _provider.Id, instId, field.LastPrice, size);
            if (_provider.VolumeIsAccumulated) {
                openInterest = field.OpenInterest - last.OpenInterest;
                turnover = field.Turnover - last.Turnover;
            }
            else {
                openInterest = field.OpenInterest;
                turnover = field.Turnover;
            }
            trade.SetMarketData(field);
            trade.SetMarketData(openInterest, turnover);
            _provider.ProcessMarketData(trade);
        }
    }
}
