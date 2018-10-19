using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using System.Net.Sockets;
using System.Windows.Forms.DataVisualization.Charting;

namespace BT_Analysis
{
    public partial class Form1 : Form
    {
        private BluetoothClient bluetoothClient;
        private BackgroundWorker bg_worker;
        private BluetoothAddress selectedAddr;
        private NetworkStream stream = null;

        private List<String> items_name = new List<String>();
        private List<BluetoothAddress> items_addr = new List<BluetoothAddress>();
        private String selectedName;
        private Queue<double> dataQueue = new Queue<double>(1024);
        private int new_point_cnt = 0;

        byte[] raw_buffer = new byte[1024];

        public Form1()
        {
            InitializeComponent();
            InitChart();

            lbl_status.Text = "Scaning ...";

            bg_worker = new BackgroundWorker();
            bg_worker.DoWork += new DoWorkEventHandler(bg_DoWork);
            bg_worker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bg_RunWorkerCompleted);

            bg_worker.RunWorkerAsync();
        }

        void bg_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            string[] items = e.Result as string[];
            foreach (string item in items)
            {
                deviceListBox.Items.Add(item);
            }
            pbSearch.Hide();
            lbl_status.Text = "Scan complete";
        }

        void bg_DoWork(object sender, DoWorkEventArgs e)
        {
            BluetoothClient scanClient = new BluetoothClient();
            BluetoothDeviceInfo[] devices = scanClient.DiscoverDevices();
            foreach (BluetoothDeviceInfo device in devices)
            {
                items_name.Add(device.DeviceName);
                items_addr.Add(device.DeviceAddress);
            }
            e.Result = items_name.ToArray();
        }

        private void btn_connect_Click(object sender, EventArgs e)
        {
            lbl_status.Text = "";
            try
            {
                bluetoothClient = new BluetoothClient();
                BluetoothEndPoint localEndpoint = new BluetoothEndPoint(selectedAddr, BluetoothService.SerialPort);
                bluetoothClient.Connect(localEndpoint);
                if (bluetoothClient.Connected)
                {
                    lbl_status.Text = selectedName + " Connected";

                    stream = bluetoothClient.GetStream();
                    tim_query.Start();
                }
            }
            catch (System.Net.Sockets.SocketException err)
            {
                Console.WriteLine(err.Message);
            }
        }

        private void btn_disconnect_Click(object sender, EventArgs e)
        {
            if (bluetoothClient != null)
            {
                bluetoothClient.Close();
                bluetoothClient = null;
                tim_query.Stop();
                lbl_status.Text = "Disconnected";
            }
        }

        private void deviceListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            selectedAddr = items_addr[deviceListBox.SelectedIndex];
            selectedName = items_name[deviceListBox.SelectedIndex];
            lbl_status.Text = "Selected Address: " + selectedAddr.ToString();
        }

        private void tim_query_Tick(object sender, EventArgs e)
        {
            //query from BT and add to queue
            new_point_cnt = 5;

            //Dequeue if too many data
            if (dataQueue.Count > 1024)
            {
                for (int i = 0; i < new_point_cnt; i++)
                {
                    dataQueue.Dequeue();
                }
            }

            //Enqueue new data
            Random r = new Random();
            for (int i = 0; i < new_point_cnt; i++)
            {
                dataQueue.Enqueue(r.Next(0, 100));
            }

            //Add point to chart
            this.chart1.Series[0].Points.Clear();
            for (int i = 0; i < dataQueue.Count; i++)
            {
                this.chart1.Series[0].Points.AddXY((i + 1), dataQueue.ElementAt(i));
            }
        }

        private void InitChart()
        {
            //Define chart area
            this.chart1.ChartAreas.Clear();
            ChartArea chartArea1 = new ChartArea("C1");
            this.chart1.ChartAreas.Add(chartArea1);
            //Define chart series
            this.chart1.Series.Clear();
            Series series1 = new Series("S1");
            series1.ChartArea = "C1";
            this.chart1.Series.Add(series1);
            //Set chartArea Apperence
            this.chart1.ChartAreas[0].AxisX.Maximum = 1024;
            this.chart1.ChartAreas[0].AxisX.Interval = 50;
            this.chart1.ChartAreas[0].AxisY.Minimum = 0;
            this.chart1.ChartAreas[0].AxisY.Maximum = 100;
            this.chart1.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart1.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            //Set chart title
            this.chart1.Titles.Clear();
            this.chart1.Titles.Add("S01");
            this.chart1.Titles[0].Text = "Pitch";
            this.chart1.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart1.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);
            //Set Chart Apperence
            this.chart1.Series[0].Color = Color.Red;
            this.chart1.Series[0].ChartType = SeriesChartType.Line;
            this.chart1.Series[0].Points.Clear();
        }
    }
}
