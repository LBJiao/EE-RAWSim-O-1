using ILOG.CPLEX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RAWSimO.SolverWrappers
{
    public class GenerateOrderandPod
    {
        public struct Orders
        {
            public int orderid;
            public int NumberofSKUs;
            public Dictionary<int, int> ListofSKUID;
            public int priority;
            public string name;
        };
        public struct Pods
        {
            public int Podid;
            public int NumberofSKUs;
            public Dictionary<int, int> ListofSKUID;
            public string name;
            //public int locationx;
            //public int locationy;
        };

        /// <summary>
        /// 对生成order和pod的相关参数的
        /// </summary>
        public static int NumberofPickstation = 4;
        public static int NumberofOrder = 30;  //50, 150 or 250
        public static double probabilityoforder = 0.4;  //订单中的SKUS的数量满足概率为probabilityoforder的几何分布
        public static double probabilityofnumberofperSKUoforder = 0.6;  //订单中的每种SKUS的数量满足概率为probabilityofnumberofperSKUoforder的几何分布
        public static int NumberofSKUsperOrder = 5;  //5或10
        public static int NumberofSKUofwarehouse = 200;  //100，500，1000
        public static double probabilityofSKUid = 5 / Convert.ToDouble(NumberofSKUofwarehouse);  //订单中的SKUS的id满足概率为probabilityofSKUid的几何分布
        public static int alpha = 5;  //2 or 3  SKUs per pod
        public static int NumberofPod = 20;  //50 or 100
        public static int CapacityofPickstation = 6;
        public static int Weightofus = 2;
        public static int levelofpriority = 5;
        public static int MAXnumberofperSKUoforder = 10;
                    #region Sets
        public int[] GenerateS()
        {
            int[] S = new int[NumberofPickstation];  // Set of currently available picking stations
            for (int i = 0; i < NumberofPickstation; i++)
                S[i] = i + 1;
            return S;
        }
        public static Dictionary<int, List<int>> Ps = new Dictionary<int, List<int>>(); //Set of pods Ps ⊆ P that are currently at station s
        Dictionary<int, int> listofSKUs = new Dictionary<int, int>();
        public Dictionary<int, Orders> GenerateOrder()
        {
            Dictionary<int, Orders> Order = new Dictionary<int, Orders>();
            Random rnd = new Random(); //在外面生成对象
            List<int> listofpriority = new List<int>();
            for (int i = 0; i < NumberofOrder; i++)
            {
                double randNum = rnd.NextDouble();
                int priority = rnd.Next(1, levelofpriority);   //s随机产生order的优先级
                                                               //int NumberofSkusofperorder = 0;
                int num = Creatnumber(probabilityoforder, randNum, NumberofSKUsperOrder);  //确定每个order的sku数量
                Orders singleOrder = new Orders
                {
                    NumberofSKUs = num,
                    priority = priority
                };
                listofpriority.Add(priority);
                Dictionary<int, int> setofsku = new Dictionary<int, int>();
                for (int j = 0; j < num; j++)
                {
                    L:
                    randNum = rnd.NextDouble();
                    int skuid = Creatnumber(probabilityofSKUid, randNum, NumberofSKUofwarehouse);   //确定每个sku的id
                    int numberofSKUid = Creatnumber(probabilityofnumberofperSKUoforder, randNum, MAXnumberofperSKUoforder);   //确定每个sku的数量
                    if (setofsku.ContainsKey(skuid))
                        goto L;
                    else
                    {
                        setofsku.Add(skuid, numberofSKUid);
                        if (listofSKUs.ContainsKey(skuid))
                            listofSKUs[skuid] += numberofSKUid;
                        else
                            listofSKUs.Add(skuid, numberofSKUid);
                    }
                }
                singleOrder.ListofSKUID = setofsku;
                singleOrder.orderid = i + 1;
                singleOrder.name = "o" + (i + 1);
                Order.Add(i + 1, singleOrder);
            }
            return Order;
        }
        public Dictionary<int, Pods> GeneratePod()
        {
            //产生pod
            Dictionary<int, Pods> Pod = new Dictionary<int, Pods>();
            Random rnd1 = new Random(); //在外面生成对象
            Dictionary<int, int> listofSKUsofpod = new Dictionary<int, int>();
            for (int i = 0; i < NumberofPod; i++)
            {
                Pods singlePod = new Pods
                {
                    NumberofSKUs = alpha,
                    Podid = i + 1
                };
                Dictionary<int, int> setofsku = new Dictionary<int, int>();
                for (int j = 0; j < singlePod.NumberofSKUs; j++)
                {
                    L1:
                    double randNum = rnd1.NextDouble();
                    int skuid = Creatnumber(probabilityofSKUid, randNum, NumberofSKUofwarehouse);   //确定每个sku的id
                    int numberofSKUid = rnd1.Next(6, 10);   //确定每个sku的数量
                    if (setofsku.ContainsKey(skuid))
                        goto L1;
                    else
                    {
                        setofsku.Add(skuid, numberofSKUid);
                        if (listofSKUs.ContainsKey(skuid))
                        {
                            if (listofSKUs[skuid] >= numberofSKUid)
                                listofSKUs[skuid] -= numberofSKUid;
                            else
                                listofSKUs.Remove(skuid);
                        };
                    }
                }
                singlePod.ListofSKUID = setofsku;
                singlePod.name = "p" + singlePod.Podid;
                Pod.Add(singlePod.Podid, singlePod);
            }
            Random rnd2 = new Random(); //在外面生成对象
            foreach (var sku in listofSKUs)
            {
                int numofsku = sku.Value;
                while (numofsku > 0)
                {
                    L2:
                    int numpod1 = rnd2.Next(1, NumberofPod);   //随机产生pod的id
                    if (Pod[numpod1].ListofSKUID.ContainsKey(sku.Key))
                        goto L2;
                    else
                    {
                        Pod[numpod1].ListofSKUID.Add(sku.Key, 5);
                        Pods pod = Pod[numpod1];
                        pod.NumberofSKUs++;
                        Pod.Remove(numpod1);
                        Pod.Add(numpod1, pod);
                    }
                    numofsku -= 5;
                }
            }
            return Pod;
        }

        public Dictionary<int, List<int>> GeneratePiSKU(Dictionary<int, Pods> Pod)
        {
            //生成PiSKU
            Dictionary<int, List<int>> PiSKU = new Dictionary<int, List<int>>(); //Set of pods P SKU i ⊆ P that include SKU i
            foreach (KeyValuePair<int, Pods> pod in Pod)
            {
                Dictionary<int, int> ListofSKUidofpod = pod.Value.ListofSKUID;
                int idofpod = pod.Key;
                foreach (var sku in ListofSKUidofpod)
                {
                    if (PiSKU.ContainsKey(sku.Key))
                        PiSKU[sku.Key].Add(idofpod);
                    else
                    {
                        List<int> listofpod = new List<int>() { idofpod };
                        PiSKU.Add(sku.Key, listofpod);
                    }
                }
            }
            return PiSKU;
        }
        public Dictionary<int, List<int>> GenerateOiSKU(Dictionary<int, Orders> Order)
        {
            //生成OiSKU
            Dictionary<int, List<int>> OiSKU = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<int, Orders> order in Order)
            {
                Dictionary<int, int> ListofSKUidoforder = order.Value.ListofSKUID;
                int idoforder = order.Key;
                foreach (var sku in ListofSKUidoforder)
                {
                    if (OiSKU.ContainsKey(sku.Key))
                        OiSKU[sku.Key].Add(idoforder);
                    else
                    {
                        List<int> listoforder = new List<int>() { idoforder };
                        OiSKU.Add(sku.Key, listoforder);
                    }
                }
            }
            return OiSKU;
        }
        #endregion

        #region Parameter
        public Dictionary<int, int> GenerateCs()
        {
            Dictionary<int, int> Cs = new Dictionary<int, int>(); //Current capacity of each picking station s ∈ S
            for (int j = 0; j < NumberofPickstation; j++)
                Cs.Add(j + 1, CapacityofPickstation);
            return Cs;
        }
        public double[] Generatewu()
        {
            double[] wu = new double[NumberofPickstation];
            for (int j = 0; j < NumberofPickstation; j++)
                wu[j] = Weightofus;
            return wu;
        }

        #endregion

        #region Decision variables name
        public class Symbol
        {
            public int o { get; set; }
            public int s { get; set; }
            public int skui { get; set; }
            public int p { get; set; }
            public string name { get; set;}
        }
        public Dictionary<int, List<Symbol>> CreatedeVarName(int[] S, Dictionary<int, Orders> Order, Dictionary<int, Pods> Pod, 
            Dictionary<int, List<int>> PiSKU, Dictionary<int, List<int>> OiSKU)
        {
            Dictionary<int, List<Symbol>> deVarName = new Dictionary<int, List<Symbol>>();
            List<Symbol> deVarNamexps = new List<Symbol>();  //pod p ∈ P is assigned to station s ∈ S
            foreach (KeyValuePair<int, Pods> pod in Pod)
            {
                for (int j = 0; j < NumberofPickstation; j++)
                    deVarNamexps.Add(new Symbol { p = pod.Key, s = S[j], name= "xps" + "_" + pod.Key.ToString() + "_" + S[j].ToString()});
            }
            deVarName.Add(1, deVarNamexps);
            List<Symbol> deVarNameyos = new List<Symbol>();  //order o ∈ O is assigned to station s ∈ S
            foreach (var order in Order)
            {
                for (int j = 0; j < NumberofPickstation; j++)
                    deVarNameyos.Add(new Symbol { o = order.Key, s = S[j], name = "yos" + "_" + order.Key.ToString() + "_" + S[j].ToString() });
            }
            deVarName.Add(2, deVarNameyos);
            List<Symbol> deVarNameyios = new List<Symbol>(); //SKU i ∈ Io of order o ∈ O is assigned to station s ∈ S
            int ii = 0;
            foreach (KeyValuePair<int, Orders> order in Order)
            {
                Dictionary<int, int> ListofSKUID = order.Value.ListofSKUID;
                for (int j = 0; j < NumberofPickstation; j++)
                {
                    foreach (var skuid in ListofSKUID.Keys)
                    {
                        deVarNameyios.Add(new Symbol { o = order.Key, s = S[j], skui= skuid, name = "yios" + "_" + skuid.ToString() + "_" + order.Key.ToString() + "_" + S[j].ToString() });
                        ii++;
                    }
                }
            }
            deVarName.Add(3, deVarNameyios);
            List<Symbol> deVarNameus = new List<Symbol>(); //Amount of unused capacity for a station s ∈ S
            for (int i = 0; i < NumberofPickstation; i++)
                deVarNameus.Add(new Symbol { s = S[i], name = "us" + "_" + S[i].ToString() });
            deVarName.Add(4, deVarNameus);
            List<Symbol> deVarNameziops = new List<Symbol>();
            ii = 0;
            int numberofzips = 0;
            foreach (var sku in OiSKU)
            {
                List<int> listofpod = PiSKU[sku.Key];
                foreach (var podid in listofpod)
                {
                    for (int j = 0; j < NumberofPickstation; j++)
                    {
                        numberofzips++;
                        foreach (var orderid in sku.Value)
                        {
                            deVarNameziops.Add(new Symbol { p = podid, o = orderid, s = S[j], skui = sku.Key, name = "ziops" + "_" + sku.Key.ToString() + "_" + orderid.ToString() + "_" + podid.ToString() + "_" + S[j].ToString() });
                            ii++;
                        }
                    }
                }
            }
            deVarName.Add(5, deVarNameziops);
            return deVarName;
        }
        #endregion

        public static int Creatnumber(double probability, double randNum, int I)
        {
            int numberofsku = 1;
            double jj = 0;
            for (int i = 1; i < I + 1; i++)
            {
                if ((jj <= randNum) && (randNum < (jj + probability * Math.Pow(1 - probability, i - 1))))
                {
                    numberofsku = i;
                    return numberofsku;
                }
                else
                    jj += probability * Math.Pow(1 - probability, i - 1);
            }
            return numberofsku;
        }
    }
}
