using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RAWSimO.SolverWrappers
{
    public class MP:GenerateOrderandPod
    {
        public static int ThreadCount { get; private set; }

        public static void Test2()
        {
            GenerateOrderandPod OrderandPod = new GenerateOrderandPod();
            int[] S = OrderandPod.GenerateS();
            Dictionary<int, Orders> Order = OrderandPod.GenerateOrder();
            Dictionary<int, Pods> Pod = OrderandPod.GeneratePod();
            write(Order, Pod);//将相关参数和数据存入TXT文本
            Dictionary<int, List<int>> PiSKU = OrderandPod.GeneratePiSKU(Pod);
            Dictionary<int, List<int>> OiSKU = OrderandPod.GenerateOiSKU(Order);
            Dictionary<int, int> Cs = OrderandPod.GenerateCs();
            double[] wu = OrderandPod.Generatewu();
            Dictionary<int, List<Symbol>> variableNames = OrderandPod.CreatedeVarName(S, Order, Pod, PiSKU, OiSKU);
            Test1(SolverType.Gurobi, S, Order, PiSKU, OiSKU, Pod, Cs, wu, variableNames);
        }
        public static void Test1(SolverType type, int[] S, Dictionary<int, Orders> Order, Dictionary<int, List<int>> PiSKU, Dictionary<int, List<int>> OiSKU,
           Dictionary<int, Pods> Pod, Dictionary<int, int> Cs, double[] wu, Dictionary<int, List<Symbol>> variableNames)
        {
            Console.WriteLine("Is64BitProcess: " + Environment.Is64BitProcess);
            LinearModel wrapper = new LinearModel(type, (string s) => { Console.Write(s); });
            Console.WriteLine("Setting up model and optimizing it with " + wrapper.Type);
            List<Symbol> deVarNamexps = variableNames[1];
            List<Symbol> deVarNameyos = variableNames[2];
            List<Symbol> deVarNameyios = variableNames[3];
            List<Symbol> deVarNameus = variableNames[4];
            List<Symbol> deVarNameziops = variableNames[5];
            VariableCollection<string> variablesBinary = new VariableCollection<string>(wrapper, VariableType.Binary, 0, 1, (string s) => { return s; });
            VariableCollection<string> variablesInteger1 = new VariableCollection<string>(wrapper, VariableType.Integer, 0, CapacityofPickstation, (string s) => { return s; });
            VariableCollection<string> variablesInteger2 = new VariableCollection<string>(wrapper, VariableType.Integer, 0, MAXnumberofperSKUoforder, (string s) => { return s; });
            wrapper.SetObjective(LinearExpression.Sum(deVarNamexps.Select(v => variablesBinary[v.name])) + LinearExpression.Sum(wu, deVarNameus.Select(v => variablesInteger1[v.name])), OptimizationSense.Minimize);
            int ii = 0;
            int jj = 0;
            foreach (var order in Order)
            {
                Dictionary<int, int> ListofSKUID = order.Value.ListofSKUID;
                for (int j = 0; j < NumberofPickstation; j++)
                {
                    foreach (var skuid in ListofSKUID.Keys)
                    {
                        wrapper.AddConstr(variablesBinary[deVarNameyios[ii].name] == variablesBinary[deVarNameyos[jj].name], "shi2");
                        ii++;
                    }
                    jj++;
                }
            }
            foreach (var order in Order)
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.o == order.Key).Select(v => variablesBinary[v.name])) <= 1, "shi3");

            foreach (var sku in OiSKU)
            {
                List<int> listofpod = PiSKU[sku.Key];
                foreach (var podid in listofpod)
                {
                    for (int j = 0; j < NumberofPickstation; j++)
                    {
                        wrapper.AddConstr(LinearExpression.Sum(deVarNameziops.Where(v => v.skui == sku.Key && v.p == podid && v.s == S[j]).Select(v => variablesInteger2[v.name])) <=
                            Pod[podid].ListofSKUID[sku.Key] * variablesBinary["xps" + "_" + podid.ToString() + "_" + S[j].ToString()], "shi4");
                    }
                }
            }
            foreach (var sku in OiSKU)
            {
                for (int j = 0; j < NumberofPickstation; j++)
                {
                    foreach (var orderid in sku.Value)
                        wrapper.AddConstr(LinearExpression.Sum(deVarNameziops.Where(v => v.skui == sku.Key && v.o == orderid && v.s == S[j]).Select(v => variablesInteger2[v.name]))
                            == Order[orderid].ListofSKUID[sku.Key] * variablesBinary["yios" + "_" + sku.Key.ToString() + "_" + orderid.ToString() + "_" + S[j].ToString()], "shi5");
                }
            }
            foreach (var station in Ps)
            {
                foreach (var pod in station.Value)
                    wrapper.AddConstr(variablesBinary["xps" + "_" + pod.ToString() + "_" + station.ToString()] == 1, "shi6");
            }

            for (int i = 0; i < NumberofPickstation; i++)
                wrapper.AddConstr(LinearExpression.Sum(deVarNameyos.Where(v => v.s == S[i]).Select(v => variablesBinary[v.name])) + variablesInteger1["us" + "_" + S[i]] == Cs[i + 1], "shi7");

            wrapper.Update();
            wrapper.Optimize();
            wrapper.ExportLP("lpexl.lp");
            Console.WriteLine("Solution:");
            if (wrapper.HasSolution())
            {
                Console.WriteLine("Obj: " + wrapper.GetObjectiveValue());
                double i1 = 0;
                double j1 = 0;
                double k1 = 0;
                for (int i = 1; i < variableNames.Count + 1; i++)
                {
                    List<Symbol> variableName = variableNames[i];
                    foreach (var itemName in variableName)
                    {
                        if (i < 4 && variablesBinary[itemName.name].GetValue() != 0)
                        {
                            Console.WriteLine(itemName.name + ": " + variablesBinary[itemName.name].GetValue());
                            if (i == 1)
                                i1++;
                            else if (i == 3)
                                j1++;
                            else if (i == 2)
                                k1++;
                        }
                        else if (i == 4 && variablesInteger1[itemName.name].GetValue() != 0)
                            Console.WriteLine(itemName.name + ": " + variablesInteger1[itemName.name].GetValue());
                        else if (i == 5 && variablesInteger2[itemName.name].GetValue() != 0)
                            Console.WriteLine(itemName.name + ": " + variablesInteger2[itemName.name].GetValue());
                    }
                }
                Console.WriteLine("the number of pod-station visits per order(PSE) =" + i1 / k1);
                Console.WriteLine("pile-on(Picks/PSE) =" + (j1 / i1));
            }
            else
            {
                Console.WriteLine("No solution!");
            }
        }
        public static void write(Dictionary<int, Orders> Order, Dictionary<int, Pods> Pod)
        {
            FileStream fs = new FileStream("test.txt", FileMode.Create, FileAccess.Write);
            StreamWriter sw = new StreamWriter(fs); // 创建写bai入流du
            //将相关参数和生成的order存为TXT文本
            sw.Write("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12}", NumberofPickstation, NumberofOrder, probabilityoforder, probabilityofnumberofperSKUoforder,
                NumberofSKUsperOrder, NumberofSKUofwarehouse, probabilityofSKUid, alpha, NumberofPod, CapacityofPickstation, Weightofus,
                levelofpriority, MAXnumberofperSKUoforder);
            sw.Write("\r");
            foreach (var order in Order)
            {
                sw.Write("{0} {1} {2} ", order.Value.orderid, order.Value.NumberofSKUs, order.Value.priority);
                foreach (var sku in order.Value.ListofSKUID)
                    sw.Write(sku.Key + " " + sku.Value + " ");
            }
            sw.Write("\r");
            foreach (var pod in Pod)
            {
                sw.Write("{0} {1} ", pod.Value.Podid, pod.Value.NumberofSKUs);
                foreach (var sku in pod.Value.ListofSKUID)
                    sw.Write(sku.Key + " " + sku.Value + " ");
            }
            sw.Flush();
            sw.Close(); //关闭文件
        }
    }

}
