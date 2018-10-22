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

struct Motor_state {
    public float voltage;
    public float current;
    public int rpm;
}

struct Package {
    public float pitch;
    public Motor_state left_motor;
    public Motor_state right_motor;
}

namespace BT_Analysis
{
    public partial class Form1 : Form
    {
        //Const
        private const int PACKAGE_QUERY  = 0xFF;
        private const int PACKAGE_HEADER = 0xFE;
        private const int PACKAGE_SPLIT  = 0x2C;
        private const int PACKAGE_END    = 0x0D;

        private const int SAMPLES_PER_CHART = 1024;
        private const int BUFFER_PER_PACKAGE = 1024;
        private const int AXIS_X_INTERVAL = 50;

        private const int PITCH_MAX = 90;
        private const int PITCH_MIN = -90;
        private const int VOLTAGE_MAX = 36;
        private const int VOLTAGE_MIN = -5;
        private const int CURRENT_MAX = 5;
        private const int CURRENT_MIN = -1;
        private const int RPM_MAX = 3000;
        private const int RPM_MIN = -3000;

        //Bluetooth connect
        private BluetoothClient bluetoothClient;
        private BackgroundWorker bg_worker;
        private List<String> items_name = new List<String>();
        private List<BluetoothAddress> items_addr = new List<BluetoothAddress>();
        private String selectedName;
        private BluetoothAddress selectedAddr;
        private NetworkStream stream = null;

        //Data decode
        private Queue<double> rawQueue = new Queue<double>(BUFFER_PER_PACKAGE);
        private Queue<Package> dataQueue = new Queue<Package>(SAMPLES_PER_CHART);
        private int new_buffer_cnt = 0;

        public Form1()
        {
            InitializeComponent();
            InitChart();

            //Scan device in background
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

                    //start receive data
                    stream = bluetoothClient.GetStream();
                    rawQueue.Clear();
                    dataQueue.Clear();
                    tim_query.Start();
                    Task.Factory.StartNew(() =>
                    {
                        while (bluetoothClient.Connected) {
                            if (stream.CanRead) {
                                rawQueue.Enqueue(stream.ReadByte());
                            }
                        }
                    });
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
                dataQueue.Clear();
                tim_query.Start();
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
            //Request data
            stream.WriteByte(PACKAGE_QUERY);

            //raw data count
            new_buffer_cnt = rawQueue.Count;

            if (new_buffer_cnt > 3) {
                //decode new data
                byte incoming_byte;
                bool should_record = false;
                byte[] raw_byte = new byte[64];
                int raw_byte_ptr = 0;

                for (int i = 0; i < new_buffer_cnt; i++)
                {
                    incoming_byte = (byte)rawQueue.Dequeue();
                    if (should_record) {
                        if (incoming_byte == PACKAGE_END)
                        {
                            //complete one new data
                            should_record = false;
                            string new_data_str = System.Text.Encoding.UTF8.GetString(raw_byte, 0, raw_byte_ptr);
                            string[] new_datas = new_data_str.Split(',');
                            Package new_package = new Package();

                            new_package.pitch               = float.Parse(new_datas[0], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.left_motor.voltage  = float.Parse(new_datas[1], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.left_motor.current  = float.Parse(new_datas[2], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.left_motor.rpm      = int.Parse(  new_datas[3], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.right_motor.voltage = float.Parse(new_datas[4], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.right_motor.current = float.Parse(new_datas[5], System.Globalization.CultureInfo.InvariantCulture);
                            new_package.right_motor.rpm     = int.Parse(  new_datas[6], System.Globalization.CultureInfo.InvariantCulture);

                            //Dequeue if too many data
                            if (dataQueue.Count > 1024)
                            {
                                dataQueue.Dequeue();
                            }

                            //Enqueue new data
                            dataQueue.Enqueue(new_package);

                            //reset buffer pointer
                            raw_byte_ptr = 0;

                            //Add point to chart
                            this.chart1.Series[0].Points.Clear();
                            this.chart2.Series[0].Points.Clear();
                            this.chart2.Series[1].Points.Clear();
                            this.chart2.Series[2].Points.Clear();
                            this.chart3.Series[0].Points.Clear();
                            this.chart3.Series[1].Points.Clear();
                            this.chart3.Series[2].Points.Clear();
                            for (int idx = 0; idx < dataQueue.Count; idx++)
                            {
                                this.chart1.Series[0].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).pitch);
                                this.chart2.Series[0].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).left_motor.voltage);
                                this.chart2.Series[1].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).left_motor.current);
                                this.chart2.Series[2].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).left_motor.rpm);
                                this.chart3.Series[0].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).right_motor.voltage);
                                this.chart3.Series[1].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).right_motor.current);
                                this.chart3.Series[2].Points.AddXY((idx + 1), dataQueue.ElementAt(idx).right_motor.rpm);
                            }
                        }
                        else {
                            //receiving byte
                            raw_byte[raw_byte_ptr] = incoming_byte;
                            raw_byte_ptr++;
                        }
                    }
                    if (incoming_byte == PACKAGE_HEADER)
                    {
                        should_record = true;
                    }
                }
            }
        }

        private void InitChart()
        {
            //Define chart area
            this.chart1.ChartAreas.Clear();
            ChartArea chartArea1 = new ChartArea("C1");
            this.chart1.ChartAreas.Add(chartArea1);
            this.chart2.ChartAreas.Clear();
            ChartArea chartArea2 = new ChartArea("C2");
            this.chart2.ChartAreas.Add(chartArea2);
            this.chart3.ChartAreas.Clear();
            ChartArea chartArea3 = new ChartArea("C3");
            this.chart3.ChartAreas.Add(chartArea3);
            
            //Define chart series
            this.chart1.Series.Clear();
            Series series1 = new Series("Pitch");
            series1.ChartArea = "C1";
            this.chart1.Series.Add(series1);

            this.chart2.Series.Clear();
            Series series2 = new Series("Voltage_L");
            series2.ChartArea = "C2";
            this.chart2.Series.Add(series2);
            Series series3 = new Series("Current_L");
            series3.ChartArea = "C2";
            this.chart2.Series.Add(series3);
            Series series4 = new Series("RPM_L");
            series4.ChartArea = "C2";
            this.chart2.Series.Add(series4);

            this.chart3.Series.Clear();
            Series series5 = new Series("Voltage_R");
            series5.ChartArea = "C3";
            this.chart3.Series.Add(series5);
            Series series6 = new Series("Current_R");
            series6.ChartArea = "C3";
            this.chart3.Series.Add(series6);
            Series series7 = new Series("RPM_R");
            series7.ChartArea = "C3";
            this.chart3.Series.Add(series7);

            //Set chartArea Apperence
            this.chart1.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart1.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART;
            this.chart1.ChartAreas[0].AxisY.Minimum = PITCH_MIN;
            this.chart1.ChartAreas[0].AxisY.Maximum = PITCH_MAX;
            this.chart1.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart1.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;

            this.chart2.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart2.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART/2;
            this.chart2.ChartAreas[0].AxisY.Minimum = VOLTAGE_MIN;
            this.chart2.ChartAreas[0].AxisY.Maximum = VOLTAGE_MAX;
            this.chart2.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart2.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;

            this.chart3.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart3.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART/2;
            this.chart3.ChartAreas[0].AxisY.Minimum = VOLTAGE_MIN;
            this.chart3.ChartAreas[0].AxisY.Maximum = VOLTAGE_MAX;
            this.chart3.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart3.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;

            //Set chart title
            this.chart1.Titles.Clear();
            this.chart1.Titles.Add("T01");
            this.chart1.Titles[0].Text = "Pitch";
            this.chart1.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart1.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart2.Titles.Clear();
            this.chart2.Titles.Add("T02");
            this.chart2.Titles[0].Text = "Motor Left";
            this.chart2.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart2.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart3.Titles.Clear();
            this.chart3.Titles.Add("T03");
            this.chart3.Titles[0].Text = "Motor Right";
            this.chart3.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart3.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            //Set Chart Apperence
            this.chart1.Series[0].Color = Color.Red;
            this.chart1.Series[0].ChartType = SeriesChartType.Line;
            this.chart1.Series[0].Points.Clear();

            this.chart2.Series[0].Color = Color.Red;
            this.chart2.Series[0].ChartType = SeriesChartType.Line;
            this.chart2.Series[0].Points.Clear();

            this.chart2.Series[1].Color = Color.Blue;
            this.chart2.Series[1].ChartType = SeriesChartType.Line;
            this.chart2.Series[1].Points.Clear();

            this.chart2.Series[2].Color = Color.Green;
            this.chart2.Series[2].ChartType = SeriesChartType.Line;
            this.chart2.Series[2].Points.Clear();

            this.chart3.Series[0].Color = Color.Red;
            this.chart3.Series[0].ChartType = SeriesChartType.Line;
            this.chart3.Series[0].Points.Clear();

            this.chart3.Series[1].Color = Color.Blue;
            this.chart3.Series[1].ChartType = SeriesChartType.Line;
            this.chart3.Series[1].Points.Clear();

            this.chart3.Series[2].Color = Color.Green;
            this.chart3.Series[2].ChartType = SeriesChartType.Line;
            this.chart3.Series[2].Points.Clear();
        }
    }
}
