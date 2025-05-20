using RAWSimO.Core.Configurations;
using RAWSimO.Core.Elements;
using RAWSimO.Core.IO;
using RAWSimO.Core.Items;
using RAWSimO.Core.Management;
using RAWSimO.Core.Metrics;
using RAWSimO.SolverWrappers;
using RAWSimO.Toolbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static RAWSimO.Core.Management.ResourceManager;
using static System.Collections.Specialized.BitVector32;
//using static RAWSimO.Core.Control.Defaults.TaskAllocation.BalancedBotManager;

namespace RAWSimO.Core.Control.Defaults.OrderBatching
{
    /// <summary>
    /// Pod的比较器
    /// </summary>
    public class PodComparer : IEqualityComparer<Pod>
    {
        /// <summary>
        /// equal
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public bool Equals(Pod x, Pod y)
        {
            return x.ID == y.ID;
        }
        /// <summary>
        /// gethashcode
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public int GetHashCode(Pod obj)
        {
            return obj.ID.GetHashCode();
        }
    }

    /// <summary>
    /// Implements a manager that uses information of the backlog to exploit similarities in orders when assigning them.
    /// </summary>
    public class M1GManager : OrderManager
    {
        /// <summary>
        /// Creates a new instance of this manager.
        /// </summary>
        /// <param name="instance">The instance this manager belongs to.</param>
        public M1GManager(Instance instance) : base(instance) { _config = instance.ControllerConfig.OrderBatchingConfig as M1GConfiguration; }

        /// <summary>
        /// The config of this controller.
        /// </summary>
        private M1GConfiguration _config;

        /// <summary>
        /// Checks whether another order is assignable to the given station.
        /// </summary>
        /// <param name="station">The station to check.</param>
        /// <returns><code>true</code> if there is another open slot, <code>false</code> otherwise.</returns>
        private bool IsAssignable(OutputStation station)
        { return station.Active && station.CapacityReserved + station.CapacityInUse < station.Capacity; }
        /// <summary>
        /// Checks whether another order is assignable to the given station.
        /// </summary>
        /// <param name="station">The station to check.</param>
        /// <returns><code>true</code> if there is another open slot and another one reserved for fast-lane, <code>false</code> otherwise.</returns>
        private bool IsAssignableKeepFastLaneSlot(OutputStation station)
        { return station.Active && station.CapacityReserved + station.CapacityInUse < station.Capacity - 1; }
        /// <summary>
        /// 已完成分配的变量集合
        /// </summary>
        public Dictionary<ItemDescription, int> _itemofPiSKU;
        /// <summary>
        /// Od约束是否需要执行
        /// </summary>
        private bool IsOd = false;
        /// <summary>
        /// order进入Od的截止时间
        /// </summary>
        private double DueTimeOrderofMP = TimeSpan.FromMinutes(30).TotalSeconds;
        /// <summary>
        /// 决策变量的命名
        /// </summary>
        private Dictionary<int, List<Symbol>> _IsvariableNames = new Dictionary<int, List<Symbol>>();
        /// <summary>
        /// Checks whether an item matching the description is contained in this pod. 
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int IsAvailabletoPiSKU(ItemDescription item) { return _itemofPiSKU.ContainsKey(item) ? _itemofPiSKU[item] : 0; }
        /// <summary>
        /// 生成PiSKU
        /// </summary>
        /// <param name="allPods"></param>
        /// <returns></returns>
        public Dictionary<ItemDescription, List<Pod>> GeneratePiSKU(IEnumerable<Pod> allPods)
        {
            Dictionary<ItemDescription, List<Pod>> PiSKU = new Dictionary<ItemDescription, List<Pod>>();
            _itemofPiSKU = new Dictionary<ItemDescription, int>();
            //生成PiSKU
            foreach (Pod pod in allPods)
            {
                IEnumerable<ItemDescription> ListofSKUidofpod = pod.ItemDescriptionsContained.Where(v => pod.IsContained(v));
                foreach (var sku in ListofSKUidofpod)
                {
                    if (PiSKU.ContainsKey(sku))
                    {
                        PiSKU[sku].Add(pod);
                        _itemofPiSKU[sku] += pod.CountAvailable(sku);
                    }
                    else
                    {
                        List<Pod> listofpod = new List<Pod>() { pod };
                        PiSKU.Add(sku, listofpod);
                        _itemofPiSKU[sku] = pod.CountAvailable(sku);
                    }
                }
            }
            return PiSKU;
        }
        /// <summary>
        /// 生成OiSKU
        /// </summary>
        /// <param name="pendingOrders"></param>
        /// <returns></returns>
        public Dictionary<ItemDescription, List<Order>> GenerateOiSKU(HashSet<Order> pendingOrders)
        {
            Dictionary<ItemDescription, List<Order>> OiSKU = new Dictionary<ItemDescription, List<Order>>();
            //生成OiSKU
            foreach (var order in pendingOrders)
            {
                IEnumerable<KeyValuePair<ItemDescription, int>> ListofSKUidoforder = order.Positions;
                foreach (var sku in ListofSKUidoforder)
                {
                    if (OiSKU.ContainsKey(sku.Key))
                        OiSKU[sku.Key].Add(order);
                    else
                    {
                        List<Order> listoforder = new List<Order>() { order };
                        OiSKU.Add(sku.Key, listoforder);
                    }
                }
            }
            return OiSKU;
        }
        /// <summary>
        /// 产生Ps
        /// </summary>
        /// <param name="Cs"></param>
        /// <returns></returns>
        public Dictionary<OutputStation, HashSet<Pod>> GeneratePs(Dictionary<OutputStation, int> Cs)
        {
            Dictionary<OutputStation, HashSet<Pod>> inboundPods = new Dictionary<OutputStation, HashSet<Pod>>();
            foreach (var station in Cs.Keys)
            {
                HashSet<Pod> hspod = new HashSet<Pod>();
                foreach (var pod in station.InboundPods)
                    hspod.Add(pod);
                inboundPods.Add(station, hspod);
                //foreach (var pod in station.InboundPods)
                //{
                //    if (Instance.ResourceManager._usedPods[pod].CurrentTask is RestTask)
                //        inboundPods[station].Remove(pod);
                //}
            }
            return inboundPods;
        }
        /// <summary>
        /// 产生Od
        /// </summary>
        /// <param name="pendingOrders"></param>
        /// <param name="PiSKU"></param>
        /// <returns></returns>
        public HashSet<Order> GenerateOd(HashSet<Order> pendingOrders, Dictionary<ItemDescription, List<Pod>> PiSKU)
        {
            HashSet<Order> Od = new HashSet<Order>();
            foreach (Order order in pendingOrders)
            {
                order.Timestay = order.DueTime - (Instance.SettingConfig.StartTime.AddSeconds(Convert.ToInt32(Instance.Controller.CurrentTime)) - order.TimePlaced).TotalSeconds;
                if (order.Timestay < DueTimeOrderofMP)
                {
                    bool Isadd = true;
                    IEnumerable<KeyValuePair<ItemDescription, int>> ListofSKUidoforder = order.Positions;
                    foreach (var sku in ListofSKUidoforder)
                    {
                        if (PiSKU[sku.Key].All(v => v.CountAvailable(sku.Key) >= sku.Value && Instance.ResourceManager.UnusedPods.Contains(v)))
                            continue;
                        else
                            Isadd = false;
                    }
                    if (Isadd)
                        Od.Add(order);
                }
            }
            int i = 0;
            foreach (Order order in pendingOrders.OrderBy(v => v.Timestay).ThenBy(u => u.DueTime)) //先选剩余的截止时间最短的，再选开始时间最早的
            {
                order.sequence = i;
                i++;
            }
            return Od;
        }
        /// <summary>
        /// 产生Cs
        /// </summary>
        /// <returns></returns>
        public new Dictionary<OutputStation, int> GenerateCs()
        {
            Dictionary<OutputStation, int> Cs = new Dictionary<OutputStation, int>();
            foreach (var station in Instance.OutputStations.Where(v => v.Capacity - v.CapacityReserved - v.CapacityInUse > 0))
                Cs.Add(station, station.Capacity - station.CapacityReserved - station.CapacityInUse);
            return Cs;
        }
        /// <summary>
        /// 产生决策变量的name
        /// </summary>
        /// <param name="PiSKU"></param>
        /// <param name="OiSKU"></param>
        /// <param name="allPods"></param>
        /// <param name="pendingOrders"></param>
        /// <param name="Cs"></param>
        /// <param name="R"></param>
        /// <param name="Pa1"></param>
        /// <param name="Pa"></param>
        /// <returns></returns>
        public Dictionary<int, List<Symbol>> CreatedeVarName(Dictionary<ItemDescription, List<Pod>> PiSKU, Dictionary<ItemDescription, List<Order>> OiSKU,
            IEnumerable<Pod> allPods, HashSet<Order> pendingOrders, Dictionary<OutputStation, int> Cs, HashSet<Bot> R, HashSet<Pod> Pa1, out HashSet<Pod> Pa)
        {
            Dictionary<int, List<Symbol>> variableNames = new Dictionary<int, List<Symbol>>();
            List<Symbol> deVarNamexps = new List<Symbol>();  //pod p ∈ P is assigned to station s ∈ S
            foreach (var pod in allPods)
            {
                foreach (var outputstation in Cs.Keys)
                    deVarNamexps.Add(new Symbol { pod = pod, outputstation = outputstation, name = "xps" + "_" + pod.ID.ToString() + "_" + outputstation.ID.ToString() });
            }
            variableNames.Add(1, deVarNamexps);
            List<Symbol> deVarNameyos = new List<Symbol>();  //order o ∈ O is assigned to station s ∈ S
            List<Symbol> deVarNameyaos = new List<Symbol>();  //order o ∈ O is assigned to station s ∈ S
            foreach (var order in pendingOrders)
            {
                foreach (var outputstation in Cs.Keys)
                {
                    deVarNameyos.Add(new Symbol { order = order, outputstation = outputstation, name = "yos" + "_" + order.ID.ToString() + "_" + outputstation.ID.ToString() });
                    deVarNameyaos.Add(new Symbol { order = order, outputstation = outputstation, name = "yaos" + "_" + order.ID.ToString() + "_" + outputstation.ID.ToString() });
                }
            }
            variableNames.Add(2, deVarNameyos);
            variableNames.Add(3, deVarNameyaos);
            //List<Symbol> deVarNameyios = new List<Symbol>(); //SKU i ∈ Io of order o ∈ O is assigned to station s ∈ S
            //foreach (var order in pendingOrders)
            //{
            //    IEnumerable<KeyValuePair<ItemDescription, int>> ListofSKUID = order.Positions;
            //    foreach (var outputstation in Cs.Keys)
            //    {
            //        foreach (var skuid in ListofSKUID)
            //            deVarNameyios.Add(new Symbol
            //            {
            //                order = order,
            //                outputstation = outputstation,
            //                skui = skuid.Key,
            //                name = "yios" + "_" + skuid.Key.ID.ToString() + "_" +
            //                                        order.ID.ToString() + "_" + outputstation.ID.ToString()
            //            });
            //    }
            //}
            //variableNames.Add(3, deVarNameyios);
            //List<Symbol> deVarNameziops = new List<Symbol>();
            List<Symbol> deVarNamedops = new List<Symbol>();
            HashSet<Pod> podset = new HashSet<Pod>();
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))
            {
                List<Pod> listofpod = PiSKU[sku.Key];
                foreach (var pod in listofpod.Where(v => Pa1.Contains(v)))
                {
                    podset.Add(pod);
                    foreach (var outputstation in Cs.Keys)
                    {
                        foreach (var order in sku.Value)
                        {
                            //deVarNameziops.Add(new Symbol
                            //{
                            //    pod = pod,
                            //    order = order,
                            //    outputstation = outputstation,
                            //    skui = sku.Key,
                            //    name = "ziops" + "_" + sku.Key.ID.ToString() + "_" +
                            //    order.ID.ToString() + "_" + pod.ID.ToString() + "_" + outputstation.ID.ToString()
                            //});
                            if (deVarNamedops.Where(v => v.order.ID == order.ID && v.pod.ID == pod.ID && v.outputstation.ID == outputstation.ID).Count() == 0)
                            {
                                int waypointID;
                                if (pod.Waypoint != null)
                                    waypointID = pod.Waypoint.ID;
                                else if (pod.Bot.CurrentWaypoint != null)
                                    waypointID = pod.Bot.CurrentWaypoint.ID;
                                else
                                    waypointID = 0;
                                deVarNamedops.Add(new Symbol
                                {
                                    pod = pod,
                                    order = order,
                                    outputstation = outputstation,
                                    podwaypointID = waypointID,
                                    name = "dops" + "_" + order.ID.ToString() + "_" + pod.ID.ToString() + "_" + outputstation.ID.ToString()
                                });
                            }
                        }
                    }
                }
            }
            //variableNames.Add(4, deVarNameziops);
            variableNames.Add(6, deVarNamedops.Distinct().ToList());
            List<Symbol> deVarNameyrp = new List<Symbol>();
            foreach (var robot in R)
            {
                foreach (var pod in allPods)
                    deVarNameyrp.Add(new Symbol { pod = pod, robot = robot, name = "yrp" + "_" + robot.ID.ToString() + "_" + pod.ID.ToString() });
            }
            variableNames.Add(4, deVarNameyrp);
            List<Symbol> deVarNameus = new List<Symbol>();
            foreach (var outputstation in Cs.Keys)
                deVarNameus.Add(new Symbol { outputstation = outputstation, name = "us" + "_" + outputstation.ID.ToString() });
            variableNames.Add(5, deVarNameus);
            Pa = new HashSet<Pod>();
            foreach (var pod in Pa1.Where(v => podset.Contains(v)))
            {
                Pa.Add(pod);
            }
            return variableNames;
        }
        /// <summary>
        /// Initializes this controller.
        /// </summary>
        /// <param name="PiSKU"></param>
        /// <param name="OiSKU"></param>
        /// <param name="variableNames"></param>
        /// <param name="Cs"></param>
        /// <param name="pendingOrders"></param>
        /// <param name="inboundPods"></param>
        /// <param name="Ra"></param>
        /// <param name="Rb"></param>
        /// <param name="R"></param>
        /// <param name="Pb"></param>
        /// <param name="Pa"></param>
        /// <param name="PodToBot"></param>
        /// <returns></returns>
        private HashSet<Pod> Initialize(out Dictionary<ItemDescription, List<Pod>> PiSKU, out Dictionary<ItemDescription, List<Order>> OiSKU,
            out Dictionary<int, List<Symbol>> variableNames, out Dictionary<OutputStation, int> Cs, out HashSet<Order> pendingOrders,
            out Dictionary<OutputStation, HashSet<Pod>> inboundPods, out HashSet<Bot> Ra, out HashSet<Bot> Rb,
            out HashSet<Bot> R, out HashSet<Pod> Pb, out HashSet<Pod> Pa, out Dictionary<Pod, Bot> PodToBot)
        {
            HashSet<Order> pendingOrders1 = new HashSet<Order>(_pendingOrders.Where(o => o.Positions.All(p => Instance.StockInfo.GetActualStock(p.Key) >= p.Value)));
            OiSKU = GenerateOiSKU(pendingOrders1);
            Cs = GenerateCs();
            inboundPods = GeneratePs(Cs);
            HashSet<ItemDescription> ItemofOiSKU = new HashSet<ItemDescription>(OiSKU.Keys);
            HashSet<Pod> allPods = new HashSet<Pod>();
            HashSet<Order> Od = new HashSet<Order>();
            Ra = new HashSet<Bot>();
            Rb = new HashSet<Bot>();
            R = new HashSet<Bot>();
            Pb = new HashSet<Pod>();
            PodToBot = new Dictionary<Pod, Bot>();
            HashSet<Pod> Pa1 = new HashSet<Pod>();
            foreach (var pods in inboundPods)//allPods包含三部分，第一部分是正在到达工作站路上的pod
            {
                foreach (Pod pod in pods.Value)
                {
                    allPods.Add(pod);
                    if (Instance.ResourceManager._usedPods.ContainsKey(pod))
                    {
                        Rb.Add(Instance.ResourceManager._usedPods[pod]);
                        R.Add(Instance.ResourceManager._usedPods[pod]);
                        PodToBot.Add(pod, Instance.ResourceManager._usedPods[pod]);
                    }
                    else //if(Instance.ResourceManager.BottoPod.ContainsValue(pod))
                    {
                        Rb.Add(Instance.ResourceManager.BottoPod.Where(V => V.Value.ID == pod.ID).First().Key);
                        R.Add(Instance.ResourceManager.BottoPod.Where(V => V.Value.ID == pod.ID).First().Key);
                        PodToBot.Add(pod, Instance.ResourceManager.BottoPod.Where(V => V.Value.ID == pod.ID).First().Key);
                    }
                    Pb.Add(pod);
                }
            }
            foreach (var pod in Instance.ResourceManager.UnusedPods.Where(v => v.IsAvailabletoOiSKU(ItemofOiSKU) && !Instance.ResourceManager.BottoPod.ContainsValue(v)
            && !Instance.ResourceManager._usedPods.ContainsKey(v)))//第二部分是在存储区域的pod
            {
                allPods.Add(pod);
                Pa1.Add(pod);
            }
            //foreach (var bot in Instance._outputstationbots.Where(v => v.CurrentTask is ExtractTask || v.CurrentTask is ParkPodTask))
            //    RR.Add(bot);
            foreach (var bot in Instance._outputstationbots)
            {
                //if (bot.Pod == null && (!Instance.ResourceManager._usedPods.ContainsValue(bot) || bot.CurrentTask is DummyTask) && !Instance.ResourceManager.BottoPod.ContainsKey(bot)) //
                //{
                //    R.Add(bot);
                //    Ra.Add(bot);
                //}
                if (bot.Pod == null && !Instance.ResourceManager._usedPods.ContainsValue(bot) && !Instance.ResourceManager.BottoPod.ContainsKey(bot)) //
                {
                    R.Add(bot);
                    Ra.Add(bot);
                }
                else if (bot.Pod == null && !Instance.ResourceManager.BottoPod.ContainsKey(bot) && !Rb.Contains(bot) && bot.CurrentTask is RestTask && bot.GetInfoDestinationWaypoint() == null)
                {
                    R.Add(bot);
                    Ra.Add(bot);
                }
            }
            //if (R.Count() == 0) 
            //    Thread.Sleep(1);
            PiSKU = GeneratePiSKU(allPods);
            pendingOrders = new HashSet<Order>(pendingOrders1.Where(o => o.Positions.All(p => IsAvailabletoPiSKU(p.Key) >= p.Value)));
            Od = GenerateOd(pendingOrders, PiSKU);
            //Od.Clear();
            //if (Cs.Sum(v => v.Value) < Od.Count)
            //    pendingOrders = Od;
            //else if (Od.Count > 0)
            //    IsOd = true;
            if (Od.Count > 0 && Cs.Sum(v => v.Value) < Od.Count)
            {
                pendingOrders.Clear();
                pendingOrders = new HashSet<Order>(Od);
            }
            OiSKU = GenerateOiSKU(pendingOrders);
            variableNames = CreatedeVarName(PiSKU, OiSKU, allPods, pendingOrders, Cs, R, Pa1, out Pa);
            return allPods;
        }
        /// <summary>
        /// Clear the queue of a station.
        /// </summary>
        /// <param name="Cs"></param>
        public void StationQueueClear(Dictionary<OutputStation, int> Cs)
        {
            foreach (var station in Cs.Where(v => v.Value > 0))
            {
                //Instance.Controller.Allocator.ClearQueue(station.Key);
                Instance.ResourceManager._QueueZiops[station.Key].Clear();
            }
        }
        /// <summary>
        /// 运用数学规划方法进行求解
        /// </summary>
        /// <param name="type"></param>
        /// <param name="PiSKU"></param>
        /// <param name="OiSKU"></param>
        /// <param name="Pods"></param>
        /// <param name="Cs"></param>
        /// <param name="variableNames"></param>
        /// <param name="pendingOrders"></param>
        /// <param name="inboundPods"></param>
        /// <param name="Ra"></param>
        /// <param name="Rb"></param>
        /// <param name="R"></param>
        /// <param name="Pb"></param>
        /// <param name="Pa"></param>
        /// <param name="PodToBot"></param>
        /// <returns></returns>
        public Dictionary<Symbol, int> solve(SolverType type, Dictionary<ItemDescription, List<Pod>> PiSKU, Dictionary<ItemDescription, List<Order>> OiSKU,
            IEnumerable<Pod> Pods, Dictionary<OutputStation, int> Cs, Dictionary<int, List<Symbol>> variableNames, HashSet<Order> pendingOrders, Dictionary<OutputStation,
                HashSet<Pod>> inboundPods, HashSet<Bot> Ra, HashSet<Bot> Rb, HashSet<Bot> R, HashSet<Pod> Pb, HashSet<Pod> Pa, Dictionary<Pod, Bot> PodToBot)
        {
            LinearModel wrapper = new LinearModel(type, (string s) => { Console.Write(s); });
            Dictionary<Symbol, int> NewZiops = new Dictionary<Symbol, int>();
            Dictionary<Symbol, int> NewZiops1 = new Dictionary<Symbol, int>();
            List<Symbol> deVarNamexps = variableNames[1];
            List<Symbol> deVarNameyos = variableNames[2];
            List<Symbol> deVarNameyaos = variableNames[3];
            List<Symbol> deVarNameyrp = variableNames[4];
            List<Symbol> deVarNameus = variableNames[5];
            List<Symbol> deVarNamedops = variableNames[6];
            double w1 = 1;
            double w2 = -1;
            double w3 = 10000;
            //double w4 = 2;
            VariableCollection<string> variablesBinary = new VariableCollection<string>(wrapper, VariableType.Binary, 0, 1, (string s) => { return s; });
            VariableCollection<string> variablesInteger2 = new VariableCollection<string>(wrapper, VariableType.Integer, 0, 5, (string s) => { return s; });
            VariableCollection<string> variablesInteger3 = new VariableCollection<string>(wrapper, VariableType.Integer, 0, 6, (string s) => { return s; });
            if (Ra.Count() > 0)
                wrapper.SetObjective((LinearExpression.Sum(deVarNamexps.Where(u => Cs.Keys.Contains(u.outputstation) && Instance.ResourceManager.UnusedPods.Contains(u.pod)).Select(v => variablesBinary[v.name] * DistanceSet[v.outputstation.Waypoint.ID][v.podwaypointID]))
                    + LinearExpression.Sum(deVarNameyrp.Where(u => Ra.Contains(u.robot) && Instance.ResourceManager.UnusedPods.Contains(u.pod)).Select(v => variablesBinary[v.name] *
                    Distances.CalculateManhattan1(v.robot.CurrentWaypoint, v.pod.Waypoint)))) * w1 + LinearExpression.Sum(deVarNameyos.Select(v => variablesBinary[v.name])) * w2
                    + LinearExpression.Sum(deVarNameus.Select(v => variablesInteger3[v.name])) * w3, OptimizationSense.Minimize);
            else
                wrapper.SetObjective(LinearExpression.Sum(deVarNamexps.Where(u => Cs.Keys.Contains(u.outputstation) && Instance.ResourceManager.UnusedPods.Contains(u.pod)).Select(v => variablesBinary[v.name] * DistanceSet[v.outputstation.Waypoint.ID][v.podwaypointID])) * w1
                    + LinearExpression.Sum(deVarNameyos.Select(v => variablesBinary[v.name])) * w2
                    + LinearExpression.Sum(deVarNameus.Select(v => variablesInteger3[v.name])) * w3, OptimizationSense.Minimize);
            foreach (var order in pendingOrders)//每个订单最多只能分配给一个工作站
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.order.ID == order.ID).Select(v => variablesBinary[v.name])) <= 1, "shi2");
            foreach (var order in pendingOrders)//当订单分配给工作站时，订单一定能够被分配给工作站
            {
                foreach (var station in Cs.Keys)
                    wrapper.AddConstr(variablesBinary["yaos" + "_" + order.ID.ToString() + "_" + station.ID.ToString()] <= variablesBinary
                        ["yos" + "_" + order.ID.ToString() + "_" + station.ID.ToString()], "shi3");
            }
            foreach (var station in Cs.Keys)//分派到工作站的订单数量必须等于工作站的可利用容量减去未被利用的容量
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyaos.Where(v => v.outputstation.ID == station.ID).Select(v => variablesBinary[v.name])) == Cs[station] - variablesInteger3["us" + "_" + station.ID.ToString()], "shi4");
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))//由分配给工作站w的货架p满足订单o中SKU i 数量不能大于货架p中存储的SKU i 的数量
            {
                foreach (var instance in Cs.Keys)
                {
                    wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.outputstation.ID == instance.ID && sku.Value.Contains(v.order)).Select(v => v.order.PositionOverallCount(sku.Key)
                    * variablesBinary[v.name])) <= LinearExpression.Sum(deVarNamexps.Where(v => v.outputstation.ID == instance.ID && PiSKU[sku.Key].Contains(v.pod)).Select(v =>
                    v.pod.CountAvailable(sku.Key) * variablesBinary[v.name])), "shi5");
                }
            }
            foreach (var pod in Pods)//货架最多只能分配给1个工作站
                wrapper.AddConstr(LinearExpression.Sum(deVarNamexps.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])) <= 1, "shi6");
            foreach (var station in inboundPods)
            {
                foreach (var pod in station.Value)
                {
                    wrapper.AddConstr(variablesBinary["xps" + "_" + pod.ID.ToString() + "_" + station.Key.ID.ToString()] == 1, "shi7");//继承系统中已经分配而未释放的货架
                    wrapper.AddConstr(variablesBinary["yrp" + "_" + PodToBot[pod].ID.ToString() + "_" + pod.ID.ToString()] == 1, "shi11");//继承系统中已经分配的机器人
                }
            }
            foreach (var pod in Pods)//当货架被分配给工作站时，必须有对应的机器人分配给货架。
                wrapper.AddConstr(LinearExpression.Sum(deVarNamexps.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])) <= LinearExpression.Sum(deVarNameyrp.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])), "shi8");
            foreach (var pod in Pods)//每个货架最多只能被一个机器人运输
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyrp.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])) <= 1, "shi9");
            foreach (var robot in R)//每个机器人最多只能分配给一个货架
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyrp.Where(v => v.robot.ID == robot.ID).Select(v => variablesBinary[v.name])) <= 1, "shi10");
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))//当ziops和yaos都大于0时，dops一定大于0
            {
                List<Pod> listofpod = PiSKU[sku.Key];
                foreach (var pod in listofpod.Where(v => Pa.Contains(v)))
                {
                    foreach (var order in sku.Value)
                    {
                        foreach (var station in Cs.Keys)
                        {
                            wrapper.AddConstr(2 * variablesBinary["dops" + "_" + order.ID.ToString() + "_" + pod.ID.ToString() + "_" + station.ID.ToString()]
                                <= variablesBinary["yaos" + "_" + order.ID.ToString() + "_" + station.ID.ToString()] + variablesBinary["xps" + "_" + pod.ID.ToString() + "_" + station.ID.ToString()], "shi12");
                        }
                    }
                }
            }
            foreach (var pod in Pa)//保证新分配的pod中最少有一个对应的订单分配
            {
                foreach (var station in Cs.Keys)
                    wrapper.AddConstr(variablesBinary["xps" + "_" + pod.ID.ToString() + "_" + station.ID.ToString()]
                        <= LinearExpression.Sum(deVarNamedops.Where(v => v.pod.ID == pod.ID && v.outputstation.ID == station.ID).Select(v => variablesBinary[v.name])), "shi13");
            }
            wrapper.Update();
            wrapper.Optimize();
            if (wrapper.HasSolution())
            {
                Dictionary<OutputStation, List<Order>> _availableStationorder = new Dictionary<OutputStation, List<Order>>();
                List<Symbol> IsdeVarNamexps = new List<Symbol>();
                List<Symbol> IsdeVarNameyaos = new List<Symbol>();
                List<Symbol> IsdeVarNameyos = new List<Symbol>();
                List<Symbol> IsdeVarNameyrp = new List<Symbol>();
                List<Symbol> IsdeVarNamedops = new List<Symbol>();
                for (int i = 1; i < variableNames.Count + 1; i++)
                {
                    List<Symbol> variableName = variableNames[i];
                    foreach (var itemName in variableName)
                    {
                        if (i < 4 && Math.Round(variablesBinary[itemName.name].GetValue()) != 0)
                        {
                            if (i == 1)
                                IsdeVarNamexps.Add(itemName);
                            else if (i == 2)
                                IsdeVarNameyos.Add(itemName);
                            else if (i == 3)
                            {
                                IsdeVarNameyaos.Add(itemName);
                                if (_availableStationorder.ContainsKey(itemName.outputstation))
                                    _availableStationorder[itemName.outputstation].Add(itemName.order);
                                else
                                {
                                    List<Order> listorder = new List<Order>
                                    {
                                        itemName.order
                                    };
                                    _availableStationorder.Add(itemName.outputstation, listorder);
                                }
                            }
                        }
                        else if (i == 4 && Math.Round(variablesBinary[itemName.name].GetValue()) != 0)
                        {
                            if (Ra.Contains(itemName.robot))
                            {
                                IsdeVarNameyrp.Add(itemName);
                                Instance.ResourceManager.BottoPod.Add(itemName.robot, itemName.pod);
                                foreach (var xps in IsdeVarNamexps.Where(v => v.pod.ID == itemName.pod.ID))
                                    xps.outputstation.RegisterInboundPod(itemName.pod);
                            }
                        }
                        else if (i == 6 && Math.Round(variablesBinary[itemName.name].GetValue()) != 0)
                            IsdeVarNamedops.Add(itemName);
                    }
                }
                _IsvariableNames.Add(1, IsdeVarNameyaos);
                //模型外求Ziops
                DateTime A = DateTime.Now;
                if (_availableStationorder.Count > 0)
                {
                    foreach (var _currentStationorder in _availableStationorder)
                    {
                        Dictionary<ItemDescription, Dictionary<Pod, int>> _availableCounts = new Dictionary<ItemDescription, Dictionary<Pod, int>>();
                        // Get current pod content
                        foreach (var itemName in IsdeVarNamexps.Where(v => v.outputstation.ID == _currentStationorder.Key.ID))
                        {
                            foreach (var item in itemName.pod.ItemDescriptionsContained.Where(v => itemName.pod.CountAvailable(v) > 0))
                            {
                                if (_availableCounts.ContainsKey(item))
                                {
                                    if (_availableCounts[item].ContainsKey(itemName.pod))
                                        _availableCounts[item][itemName.pod] += itemName.pod.CountAvailable(item);
                                    else
                                        _availableCounts[item].Add(itemName.pod, itemName.pod.CountAvailable(item));
                                }
                                else
                                {
                                    Dictionary<Pod, int> Counts = new Dictionary<Pod, int>();
                                    _availableCounts.Add(item, Counts);
                                    if (_availableCounts[item].ContainsKey(itemName.pod))
                                        _availableCounts[item][itemName.pod] += itemName.pod.CountAvailable(item);
                                    else
                                        _availableCounts[item].Add(itemName.pod, itemName.pod.CountAvailable(item));
                                }
                            }
                        }
                        List<Pod> dopspods = new List<Pod>();
                        // Check all assigned orders
                        foreach (var order in _currentStationorder.Value)
                        {
                            // Get demand for items caused by order
                            Dictionary<ItemDescription, int> itemDemands = new Dictionary<ItemDescription, int>();
                            foreach (var item in order.Positions)
                                itemDemands.Add(item.Key, item.Value);
                            //List<Pod> dopspods = new List<Pod>();
                            if (IsdeVarNamedops.Where(v => v.order.ID == order.ID).Count() > 0)
                            {
                                foreach (var item in IsdeVarNamedops.Where(v => v.order.ID == order.ID))
                                    dopspods.Add(item.pod);
                            }
                            // Check whether sufficient inventory is still available in the pod (also make sure it is was available in the beginning, not all values were updated at the beginning of this function / see above)
                            // Update remaining pod content
                            foreach (var itemDemand in itemDemands)
                            {
                                int number = itemDemand.Value;
                                while (number > 0)
                                {
                                    Pod pod;
                                    if (_availableCounts[itemDemand.Key].Keys.Where(v => dopspods.Contains(v)).Count() > 0) //优先检索新分配的货架
                                    {
                                        pod = _availableCounts[itemDemand.Key].Keys.Where(v => dopspods.Contains(v)).First();
                                        dopspods.Remove(pod);
                                    }
                                    else
                                        pod = _availableCounts[itemDemand.Key].Keys.First();
                                    Symbol name = new Symbol
                                    {
                                        pod = pod,
                                        order = order,
                                        outputstation = _currentStationorder.Key,
                                        skui = itemDemand.Key,
                                        name = "ziops" + "_" + itemDemand.Key.ID.ToString() + "_" +
                                        order.ID.ToString() + "_" + pod.ID.ToString() + "_" + _currentStationorder.Key.ID.ToString()
                                    };
                                    if (_availableCounts[itemDemand.Key][pod] >= number)
                                    {
                                        int numpods = _availableCounts[itemDemand.Key].Keys.Where(v => dopspods.Contains(v)).Count();
                                        if (numpods > 0 && number > 1)
                                        {
                                            Pod pod1 = _availableCounts[itemDemand.Key].Keys.Where(v => dopspods.Contains(v)).First();
                                            dopspods.Remove(pod1);
                                            if (_availableCounts[itemDemand.Key][pod] >= _availableCounts[itemDemand.Key][pod1])
                                            {
                                                _availableCounts[itemDemand.Key][pod] -= number - numpods;
                                                Instance.ResourceManager._Ziops[_currentStationorder.Key].Add(name, number - numpods);
                                                NewZiops.Add(name, number - numpods);
                                                number = numpods;
                                            }
                                            else
                                            {
                                                _availableCounts[itemDemand.Key][pod] -= 1;
                                                Instance.ResourceManager._Ziops[_currentStationorder.Key].Add(name, 1);
                                                NewZiops.Add(name, 1);
                                                number = 1;
                                            }
                                        }
                                        else
                                        {
                                            _availableCounts[itemDemand.Key][pod] -= number;
                                            NewZiops.Add(name, number);
                                            Instance.ResourceManager._Ziops[_currentStationorder.Key].Add(name, number);
                                            number = 0;
                                        }
                                        if (_availableCounts[itemDemand.Key][pod] == 0)
                                            _availableCounts[itemDemand.Key].Remove(pod);
                                    }
                                    else
                                    {
                                        NewZiops.Add(name, _availableCounts[itemDemand.Key][pod]);
                                        Instance.ResourceManager._Ziops[_currentStationorder.Key].Add(name, _availableCounts[itemDemand.Key][pod]);
                                        number -= _availableCounts[itemDemand.Key][pod];
                                        _availableCounts[itemDemand.Key].Remove(pod);
                                    }
                                }

                            }
                        }
                        if (dopspods.Count > 0)
                        {
                            foreach (var pod in dopspods)
                            {
                                Symbol name2 = IsdeVarNameyrp.Where(v => v.pod.ID == pod.ID).First();
                                IsdeVarNameyrp.Remove(name2);
                                Instance.ResourceManager.BottoPod.Remove(name2.robot);
                                foreach (var xps in IsdeVarNamexps.Where(v => v.pod.ID == name2.pod.ID))
                                    xps.outputstation.UnregisterInboundPod(name2.pod);
                                Symbol name1 = IsdeVarNamexps.Where(v => v.pod.ID == pod.ID).First();
                                IsdeVarNamexps.Remove(name1);
                            }

                        }
                    }
                }
                Instance.Observer.TimeOrderBatchingbyziops((DateTime.Now - A).TotalSeconds);
            }
            //else
            //    Thread.Sleep(1);
            return NewZiops;
        }
        /// <summary>
        /// This is called to decide about potentially pending orders.
        /// This method is being timed for statistical purposes and is also ONLY called when <code>SituationInvestigated</code> is <code>false</code>.
        /// Hence, set the field accordingly to react on events not tracked by this outer skeleton.
        /// </summary>
        protected override void DecideAboutPendingOrders()
        {
            DateTime A = DateTime.Now;
            Dictionary<ItemDescription, List<Pod>> PiSKU;
            Dictionary<ItemDescription, List<Order>> OiSKU;
            Dictionary<int, List<Symbol>> variableNames;
            Dictionary<OutputStation, int> Cs;
            Dictionary<OutputStation, HashSet<Pod>> inboundPods;
            HashSet<Order> pendingOrders;
            HashSet<Bot> Ra;//可以参与分配的robot集合
            HashSet<Bot> Rb;//不可以参与分配的robot集合，即已经分配还没释放的robot集合
            HashSet<Bot> R;//所有的robot集合
            HashSet<Pod> Pb;//已被分配的pod集合
            HashSet<Pod> Pa;//未被分配的pod集合
            Dictionary<Pod, Bot> PodToBot;//已经被分配的pod-bot对
            //HashSet<Symbol> linshiSymbol;
            _IsvariableNames.Clear();
            //if(Instance.ResourceManager.BottoPod.Count()>0)
            //    Thread.Sleep(1);
            // 对相关参数进行初始化（更新）
            HashSet<Pod> allPods = Initialize(out PiSKU, out OiSKU, out variableNames, out Cs, out pendingOrders, out inboundPods, out Ra, out Rb, out R, out Pb, out Pa, out PodToBot);
            if (R.Count() > 0) //运用Gurobi求解
            {
                Dictionary<Symbol, int> NewZiops = solve(SolverType.Gurobi, PiSKU, OiSKU, allPods, Cs, variableNames, pendingOrders, inboundPods, Ra, Rb, R, Pb, Pa, PodToBot);
                if (_IsvariableNames[1].Count() > 0)
                {
                    // 将相应的order分配给station
                    //List<Symbol> IsdeVarNamexps = _IsvariableNames[0];
                    List<Symbol> IsdeVarNameyaos = _IsvariableNames[1];
                    //_IsvariableNames.Remove(0);
                    foreach (var symbol in NewZiops)
                    {
                        for (int i = 0; i < symbol.Value; i++)
                            symbol.Key.pod.JustRegisterItem(symbol.Key.skui); //将pod中选中的item进行标记
                    }
                    while (IsdeVarNameyaos.Count > 0)
                    {
                        // Assign the order
                        AllocateOrder(IsdeVarNameyaos.First().order, IsdeVarNameyaos.First().outputstation);
                        // Log fast lane assignment
                        Instance.StatCustomControllerInfo.CustomLogOB1++;
                        IsdeVarNameyaos.RemoveAt(0);
                    }
                }
                Instance.Observer.TimeOrderBatchingbyMP((DateTime.Now - A).TotalSeconds);
            }
            //else
            //    Instance.Observer.TimeOrderBatchingbyMP((DateTime.Now - A).TotalSeconds);
        }

        #region IOptimize Members

        /// <summary>
        /// Signals the current time to the mechanism. The mechanism can decide to block the simulation thread in order consume remaining real-time.
        /// </summary>
        /// <param name="currentTime">The current simulation time.</param>
        public override void SignalCurrentTime(double currentTime) { /* Ignore since this simple manager is always ready. */ }

        #endregion

        #region Custom stat tracking

        /// <summary>
        /// Contains the aggregated scorer values.
        /// </summary>
        private double[] _statScorerValues = null;
        /// <summary>
        /// Contains the number of assignments done.
        /// </summary>
        private double _statAssignments = 0;
        /// <summary>
        /// The callback indicates a reset of the statistics.
        /// </summary>
        public override void StatReset()
        {
            _statScorerValues = null;
            _statAssignments = 0;
        }
        /// <summary>
        /// The callback that indicates that the simulation is finished and statistics have to submitted to the instance.
        /// </summary>
        public override void StatFinish()
        {
            Instance.StatCustomControllerInfo.CustomLogOBString =
                _statScorerValues == null ? "" :
                string.Join(IOConstants.DELIMITER_CUSTOM_CONTROLLER_FOOTPRINT.ToString(), _statScorerValues.Select(e => e / _statAssignments).Select(e => e.ToString
                (IOConstants.FORMATTER)));
        }

        #endregion
    }

}
