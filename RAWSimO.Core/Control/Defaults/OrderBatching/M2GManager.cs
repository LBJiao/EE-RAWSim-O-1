using RAWSimO.Core.Configurations;
using RAWSimO.Core.Elements;
using RAWSimO.Core.IO;
using RAWSimO.Core.Items;
using RAWSimO.Core.Metrics;
using RAWSimO.SolverWrappers;
using RAWSimO.Toolbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static RAWSimO.Core.Management.ResourceManager;

namespace RAWSimO.Core.Control.Defaults.OrderBatching
{

    /// <summary>
    /// Implements a manager that uses information of the backlog to exploit similarities in orders when assigning them.
    /// </summary>
    public class M2GManager : OrderManager
    {
        /// <summary>
        /// Creates a new instance of this manager.
        /// </summary>
        /// <param name="instance">The instance this manager belongs to.</param>
        public M2GManager(Instance instance) : base(instance) { _config = instance.ControllerConfig.OrderBatchingConfig as M2GConfiguration; }

        /// <summary>
        /// The config of this controller.
        /// </summary>
        private M2GConfiguration _config;


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
        /// <returns></returns>
        public Dictionary<OutputStation, IEnumerable<Pod>> GeneratePs(Dictionary<OutputStation, int> Cs)
        {
            Dictionary<OutputStation, IEnumerable<Pod>> inboundPods = new Dictionary<OutputStation, IEnumerable<Pod>>();
            foreach (var station in Cs.Keys)
                inboundPods.Add(station, station.InboundPods);
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
        /// <returns></returns>
        public Dictionary<int, List<Symbol>> CreatedeVarName(Dictionary<ItemDescription, List<Pod>> PiSKU, Dictionary<ItemDescription, List<Order>> OiSKU,
            IEnumerable<Pod> allPods, HashSet<Order> pendingOrders, Dictionary<OutputStation, int> Cs)
        {
            Dictionary<int, List<Symbol>> variableNames = new Dictionary<int, List<Symbol>>();
            List<Symbol> deVarNamexps = new List<Symbol>();  //pod p ∈ P is assigned to station s ∈ S
            foreach (var pod in allPods)
            {
                foreach (var instance in Cs.Keys)
                    deVarNamexps.Add(new Symbol { pod = pod, outputstation = instance, name = "xps" + "_" + pod.ID.ToString() + "_" + instance.ID.ToString() });
            }
            variableNames.Add(1, deVarNamexps);
            List<Symbol> deVarNameyos = new List<Symbol>();  //order o ∈ O is assigned to station s ∈ S
            foreach (var order in pendingOrders)
            {
                foreach (var instance in Cs.Keys)
                    deVarNameyos.Add(new Symbol { order = order, outputstation = instance, name = "yos" + "_" + order.ID.ToString() + "_" + instance.ID.ToString() });
            }
            variableNames.Add(2, deVarNameyos);
            List<Symbol> deVarNameyios = new List<Symbol>(); //SKU i ∈ Io of order o ∈ O is assigned to station s ∈ S
            foreach (var order in pendingOrders)
            {
                IEnumerable<KeyValuePair<ItemDescription, int>> ListofSKUID = order.Positions;
                foreach (var instance in Cs.Keys)
                {
                    foreach (var skuid in ListofSKUID)
                        deVarNameyios.Add(new Symbol
                        {
                            order = order,
                            outputstation = instance,
                            skui = skuid.Key,
                            name = "yios" + "_" + skuid.Key.ID.ToString() + "_" +
                                                    order.ID.ToString() + "_" + instance.ID.ToString()
                        });
                }
            }
            variableNames.Add(3, deVarNameyios);
            List<Symbol> deVarNameyaos = new List<Symbol>();  //order o ∈ O is assigned to station s ∈ S
            foreach (var order in pendingOrders)
            {
                foreach (var instance in Cs.Keys)
                    deVarNameyaos.Add(new Symbol { order = order, outputstation = instance, name = "yaos" + "_" + order.ID.ToString() + "_" + instance.ID.ToString() });
            }
            variableNames.Add(4, deVarNameyaos);
            List<Symbol> deVarNameziops = new List<Symbol>();
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))
            {
                List<Pod> listofpod = PiSKU[sku.Key];
                foreach (var pod in listofpod)
                {
                    foreach (var instance in Cs.Keys)
                    {
                        foreach (var order in sku.Value)
                        {
                            deVarNameziops.Add(new Symbol
                            {
                                pod = pod,
                                order = order,
                                outputstation = instance,
                                skui = sku.Key,
                                name = "ziops" + "_" + sku.Key.ID.ToString() + "_" +
                                order.ID.ToString() + "_" + pod.ID.ToString() + "_" + instance.ID.ToString()
                            });
                        }
                    }
                }
            }
            variableNames.Add(5, deVarNameziops);
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
        /// <param name="Od"></param>
        /// <returns></returns>
        private IEnumerable<Pod> Initialize(out Dictionary<ItemDescription, List<Pod>> PiSKU, out Dictionary<ItemDescription, List<Order>> OiSKU, out Dictionary<int, List<Symbol>>
            variableNames, out Dictionary<OutputStation, int> Cs, out HashSet<Order> pendingOrders, out Dictionary<OutputStation, IEnumerable<Pod>> inboundPods, out HashSet<Order> Od)
        {
            HashSet<Order> pendingOrders1 = new HashSet<Order>(_pendingOrders.Where(o => o.Positions.All(p => Instance.StockInfo.GetActualStock(p.Key) >= p.Value)));
            OiSKU = GenerateOiSKU(pendingOrders1);
            Cs = GenerateCs();
            inboundPods = GeneratePs(Cs);
            HashSet<ItemDescription> ItemofOiSKU = new HashSet<ItemDescription>(OiSKU.Keys);
            HashSet<Pod> allPods = new HashSet<Pod>();
            foreach (var pods in inboundPods)
            {
                foreach (Pod pod in pods.Value)
                    allPods.Add(pod);
            }
            foreach (var pod in Instance.ResourceManager.UnusedPods.Where(v => v.IsAvailabletoOiSKU(ItemofOiSKU)))
                allPods.Add(pod);
            foreach (var station in Cs.Keys)
            {
                foreach (var pod in Instance.ResourceManager._Ziops[station].Select(v => v.Key.pod).Distinct())
                {
                    if (!allPods.Contains(pod))
                        allPods.Add(pod);
                }
            }
            PiSKU = GeneratePiSKU(allPods);
            pendingOrders = new HashSet<Order>(pendingOrders1.Where(o => o.Positions.All(p => IsAvailabletoPiSKU(p.Key) >= p.Value)));
            Od = GenerateOd(pendingOrders, PiSKU);
            //Od.Clear();
            IsOd = false;
            if (Cs.Sum(v => v.Value) < Od.Count)
                pendingOrders = Od;
            else if (Od.Count > 0)
                IsOd = true;
            OiSKU = GenerateOiSKU(pendingOrders);
            variableNames = CreatedeVarName(PiSKU, OiSKU, allPods, pendingOrders, Cs);
            return allPods;
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
        /// <param name="Od"></param>
        /// <returns></returns>
        public Dictionary<Symbol, int> solve(SolverType type, Dictionary<ItemDescription, List<Pod>> PiSKU, Dictionary<ItemDescription, List<Order>> OiSKU, IEnumerable<Pod> Pods, Dictionary<OutputStation, int> Cs,
            Dictionary<int, List<Symbol>> variableNames, HashSet<Order> pendingOrders, Dictionary<OutputStation, IEnumerable<Pod>> inboundPods, HashSet<Order> Od)
        {
            FileStream fs = new FileStream("test2.txt", FileMode.Create, FileAccess.Write);
            StreamWriter sw = new StreamWriter(fs); // 创建写bai入流du
            sw.WriteLine("Is64BitProcess: " + Environment.Is64BitProcess);
            LinearModel wrapper = new LinearModel(type, (string s) => { Console.Write(s); });
            sw.WriteLine("Setting up model and optimizing it with " + wrapper.Type);
            Dictionary<Symbol, int> NewZiops = new Dictionary<Symbol, int>();
            List<Symbol> deVarNamexps = variableNames[1];
            List<Symbol> deVarNameyos = variableNames[2];
            List<Symbol> deVarNameyios = variableNames[3];
            List<Symbol> deVarNameyaos = variableNames[4];
            List<Symbol> deVarNameziops = variableNames[5];
            double w1 = pendingOrders.Count;
            double w2 = 1 / (Cs.Sum(v => v.Value) * w1);
            VariableCollection<string> variablesBinary = new VariableCollection<string>(wrapper, VariableType.Binary, 0, 1, (string s) => { return s; });
            VariableCollection<string> variablesInteger2 = new VariableCollection<string>(wrapper, VariableType.Integer, 0, 10, (string s) => { return s; });
            //wrapper.SetObjective(LinearExpression.Sum(deVarNamexps.Select(v => variablesBinary[v.name])) * w1 - LinearExpression.Sum(deVarNameyos.Select(v => variablesBinary[v.name])), OptimizationSense.Minimize);
            wrapper.SetObjective(LinearExpression.Sum(deVarNamexps.Select(v => variablesBinary[v.name])) * w1 - LinearExpression.Sum(deVarNameyos.Select(v => variablesBinary[v.name]))
                + LinearExpression.Sum(pendingOrders.Select(u => LinearExpression.Sum(deVarNameyaos.Where(v => v.order.ID == u.ID).Select(v => variablesBinary[v.name])) * u.sequence)) * w2, OptimizationSense.Minimize);
            foreach (var order in pendingOrders)
            {
                IEnumerable<KeyValuePair<ItemDescription, int>> ListofSKUID = order.Positions;
                foreach (var instance in Cs.Keys)
                {
                    foreach (var skuid in ListofSKUID)
                    {
                        wrapper.AddConstr(variablesBinary["yios" + "_" + skuid.Key.ID.ToString() + "_" + order.ID.ToString() + "_" + instance.ID.ToString()] == variablesBinary
                            ["yos" + "_" + order.ID.ToString() + "_" + instance.ID.ToString()], "shi2");
                    }
                }
            }
            foreach (var order in pendingOrders)
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.order.ID == order.ID).Select(v => variablesBinary[v.name])) <= 1, "shi3");
            foreach (var pod in Pods)
                wrapper.AddConstr(LinearExpression.Sum(deVarNamexps.Where(v => v.pod.ID == pod.ID).Select(v => variablesBinary[v.name])) <= 1, "shi4");
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))
            {
                List<Pod> listofpod = PiSKU[sku.Key];
                foreach (var pod in listofpod)
                {
                    foreach (var instance in Cs.Keys)
                    {
                        wrapper.AddConstr(LinearExpression.Sum(deVarNameziops.Where(v => v.skui.ID == sku.Key.ID && v.pod.ID == pod.ID && v.outputstation.ID == instance.ID).Select(v =>
                        variablesInteger2[v.name])) <= pod.CountAvailable(sku.Key) * variablesBinary["xps" + "_" + pod.ID.ToString() + "_" + instance.ID.ToString()], "shi5");
                    }
                }
            }
            foreach (var sku in OiSKU.Where(v => PiSKU.ContainsKey(v.Key)))
            {
                foreach (var instance in Cs.Keys)
                {
                    foreach (var order in sku.Value)
                    {
                        wrapper.AddConstr(LinearExpression.Sum(deVarNameziops.Where(v => v.skui.ID == sku.Key.ID && v.order.ID == order.ID && v.outputstation.
                        ID == instance.ID).Select(v => variablesInteger2[v.name])) == order.GetDemandCount(sku.Key) * variablesBinary["yios" + "_" + sku.
                        Key.ID.ToString() + "_" + order.ID.ToString() + "_" + instance.ID.ToString()], "shi6");
                    }
                }
            }
            //foreach (var instance in Cs.Keys)
            //    wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.outputstation.ID == instance.ID).Select(v => variablesBinary[v.name])) >= Cs[instance], "shi7");
            foreach (var instance in Cs.Keys)
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyaos.Where(v => v.outputstation.ID == instance.ID).Select(v => variablesBinary[v.name])) == Cs[instance], "shi8");
            foreach (var order in pendingOrders)
            {
                foreach (var station in Cs.Keys)
                    wrapper.AddConstr(variablesBinary["yaos" + "_" + order.ID.ToString() + "_" + station.ID.ToString()] <= variablesBinary
                        ["yos" + "_" + order.ID.ToString() + "_" + station.ID.ToString()], "shi9");
            }
            foreach (var station in inboundPods)
            {
                foreach (var pod in station.Value)
                    wrapper.AddConstr(variablesBinary["xps" + "_" + pod.ID.ToString() + "_" + station.Key.ID.ToString()] == 1, "shi10");
            }
            if (IsOd)
            {
                foreach (var order in Od)
                    wrapper.AddConstr(LinearExpression.Sum(deVarNameyaos.Where(v => v.order.ID == order.ID).Select(v => variablesBinary[v.name])) == 1, "shi11");
                //foreach (var instance in Cs.Keys)
                //    wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.outputstation.ID == instance.ID && Od.Contains(v.order)).Select(v => variablesBinary[v.name])) <= Cs[instance], "shi8");
            }
            wrapper.Update();
            wrapper.Optimize();
            wrapper.ExportLP("lpexl.lp");
            sw.WriteLine("Solution:");
            if (wrapper.HasSolution())
            {
                sw.WriteLine("Obj: " + wrapper.GetObjectiveValue());
                double j1 = 0;
                //double j2 = 0;
                List<Symbol> IsdeVarNamexps = new List<Symbol>();
                List<Symbol> IsdeVarNameyaos = new List<Symbol>();
                List<Symbol> IsdeVarNameyos = new List<Symbol>();
                for (int i = 1; i < variableNames.Count + 1; i++)
                {
                    List<Symbol> variableName = variableNames[i];
                    foreach (var itemName in variableName)
                    {
                        if (i < 5 && Math.Round(variablesBinary[itemName.name].GetValue()) != 0)
                        {
                            sw.WriteLine(itemName.name + ": " + variablesBinary[itemName.name].GetValue());
                            if (i == 1)
                                IsdeVarNamexps.Add(itemName);
                            else if (i == 3)
                                j1++;
                            else if (i == 2)
                                IsdeVarNameyos.Add(itemName);
                            else if (i == 4)
                                IsdeVarNameyaos.Add(itemName);
                        }
                        else if (i == 5 && Math.Round(variablesInteger2[itemName.name].GetValue()) != 0)
                        {
                            sw.WriteLine(itemName.name + ": " + variablesInteger2[itemName.name].GetValue());
                            NewZiops.Add(itemName, (int)Math.Round(variablesInteger2[itemName.name].GetValue()));
                            //Instance.ResourceManager.SupplementExtractRequests(itemName.order, itemName, (int)Math.Round(variablesInteger2[itemName.name].GetValue()));
                            Instance.ResourceManager._Ziops[itemName.outputstation].Add(itemName, (int)Math.Round(variablesInteger2[itemName.name].GetValue()));
                        }
                    }
                }
                _IsvariableNames.Add(1, IsdeVarNameyaos);
                //将Instance.ResourceManager._Ziops中多余的item去掉
                if (IsdeVarNameyos.Count != IsdeVarNameyaos.Count)
                {
                    List<Symbol> deleteyos = new List<Symbol>();
                    foreach (var itemName in IsdeVarNameyos.Where(v => !IsdeVarNameyaos.Select(u => u.order.ID).Contains(v.order.ID)))
                    {
                        foreach (var item in Instance.ResourceManager._Ziops[itemName.outputstation].Where(v => v.Key.order.ID == itemName.order.ID))
                            deleteyos.Add(item.Key);
                    }
                    foreach (var itemName in deleteyos)
                    {
                        Instance.ResourceManager._Ziops[itemName.outputstation].Remove(itemName);
                        NewZiops.Remove(itemName);
                    }
                }
                sw.WriteLine("the number of pod-station visits per order(PSE) =" + IsdeVarNamexps.Count / IsdeVarNameyaos.Count);
                sw.WriteLine("pile-on(Picks/PSE) =" + (j1 / IsdeVarNamexps.Count));
            }
            else
                sw.WriteLine("No solution!");
            sw.Flush();
            sw.Close(); //关闭文件
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
            Dictionary<ItemDescription, List<Pod>> PiSKU = new Dictionary<ItemDescription, List<Pod>>();
            Dictionary<ItemDescription, List<Order>> OiSKU = new Dictionary<ItemDescription, List<Order>>();
            Dictionary<int, List<Symbol>> variableNames = new Dictionary<int, List<Symbol>>();
            Dictionary<OutputStation, int> Cs = new Dictionary<OutputStation, int>();
            HashSet<Order> Od = new HashSet<Order>();
            Dictionary<OutputStation, IEnumerable<Pod>> inboundPods = new Dictionary<OutputStation, IEnumerable<Pod>>();
            HashSet<Order> pendingOrders = new HashSet<Order>();
            // 对相关参数进行初始化（更新）
            IEnumerable<Pod> allPods = Initialize(out PiSKU, out OiSKU, out variableNames, out Cs, out pendingOrders, out inboundPods, out Od);
            //运用Gurobi求解
            Dictionary<Symbol, int> NewZiops = solve(SolverType.Gurobi, PiSKU, OiSKU, allPods, Cs, variableNames, pendingOrders, inboundPods, Od);
            // 将相应的order分配给station
            OutputStation chosenStation = null;
            Order chosenOrder = null;
            Pod chosenPod = null;
            ItemDescription chosenitem = null;
            //List<Symbol> IsdeVarNamexps = _IsvariableNames[0];
            List<Symbol> IsdeVarNameyaos = _IsvariableNames[1];
            _IsvariableNames.Remove(1);
            //_IsvariableNames.Remove(0);
            foreach (var symbol in NewZiops)
            {
                chosenPod = symbol.Key.pod;
                chosenitem = symbol.Key.skui;
                for (int i = 0; i < symbol.Value; i++)
                    chosenPod.JustRegisterItem(chosenitem); //将pod中选中的item进行标记
            }
            while (IsdeVarNameyaos.Count > 0)
            {
                chosenOrder = IsdeVarNameyaos.First().order;
                chosenStation = IsdeVarNameyaos.First().outputstation;
                // Assign the order
                AllocateOrder(chosenOrder, chosenStation);
                // Log fast lane assignment
                Instance.StatCustomControllerInfo.CustomLogOB1++;
                IsdeVarNameyaos.RemoveAt(0);
            }
            Instance.Observer.TimeOrderBatchingbyMP((DateTime.Now - A).TotalSeconds);
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
