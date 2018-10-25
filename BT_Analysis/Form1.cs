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
using System.Threading;

struct Motor_state {
    public float voltage;
    public float current;
    public int rpm;
}

struct Package {
    public double index;
    public float  pitch;
    public int    is_boost;
    public int    intent;
    public Motor_state left_motor;
    public Motor_state right_motor;
}

namespace BT_Analysis
{
    public partial class Form1 : Form
    {
        //Const
        private const int PACKAGE_QUERY = 0xFF;
        private const int PACKAGE_HEADER = 0xFE;
        private const int PACKAGE_SPLIT = 0x2C;
        private const int PACKAGE_END = 0x0D;

        private const int SAMPLES_PER_CHART = 128;
        private const int BUFFER_PER_PACKAGE = 1024;
        private const int AXIS_X_INTERVAL = 5;

        private const int PITCH_MAX = 30;
        private const int PITCH_MIN = 0;
        private const int VOLTAGE_MAX = 36;
        private const int VOLTAGE_MIN = -1;
        private const int CURRENT_MAX = 10;
        private const int CURRENT_MIN = -1;
        private const int RPM_MAX = 3000;
        private const int RPM_MIN = -3000;
        private const int INTENT_MAX = 4;
        private const int INTENT_MIN = -1;

        //Bluetooth connect
        private BluetoothClient scanClient;
        private BluetoothClient bluetoothClient;
        private BackgroundWorker bg_worker;
        private List<String> items_name = new List<String>();
        private List<BluetoothAddress> items_addr = new List<BluetoothAddress>();
        private String selectedName;
        private BluetoothAddress selectedAddr;
        private NetworkStream stream = null;
        private CancellationTokenSource source = new CancellationTokenSource();
        private CancellationToken token;

        //Data decode
        private Queue<double> rawQueue = new Queue<double>(BUFFER_PER_PACKAGE);
        private Queue<Package> dataQueue = new Queue<Package>(SAMPLES_PER_CHART);
        private int new_buffer_cnt = 0;
        private long x_axis_cnt = 0;
        private double zoom_ratio = 0;

        //Tooltips
        Point? prevPosition = null;
        ToolTip tooltip = new ToolTip();

        public Form1()
        {
            InitializeComponent();
            InitChart();

            //Scan device in background
            lbl_status.Text = "Scaning ...";
            bg_worker = new BackgroundWorker();
            bg_worker.DoWork += new DoWorkEventHandler(bg_DoWork);
            bg_worker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(bg_RunWorkerCompleted);
            scanClient = new BluetoothClient();
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
            scanClient = null;
        }

        void bg_DoWork(object sender, DoWorkEventArgs e)
        {
            BluetoothDeviceInfo[] devices = scanClient.DiscoverDevices();
            foreach (BluetoothDeviceInfo device in devices)
            {
                items_name.Add(device.DeviceName);
                items_addr.Add(device.DeviceAddress);
            }
            e.Result = items_name.ToArray();
        }

        private void receive_data(CancellationToken token)
        {
            while (bluetoothClient.Connected)
            {
                if (token.IsCancellationRequested == true)
                {
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    try
                    {
                        if (stream.CanRead)
                        {
                            rawQueue.Enqueue(stream.ReadByte());
                        }
                    }
                    catch (System.IO.IOException e)
                    {

                    }
                }
            }
        }

        private void btn_connect_Click(object sender, EventArgs e)
        {
            lbl_status.Text = "";
            textBoxQuerySpeed.Enabled = false;
            tim_query.Interval = int.Parse(textBoxQuerySpeed.Text);
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
                    Task.Factory.StartNew(() => receive_data(token), token);
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
                if (source != null) source.Cancel();
                bluetoothClient.Close();
                tim_query.Stop();
                dataQueue.Clear();
                rawQueue.Clear();
                lbl_status.Text = "Disconnected";
                textBoxQuerySpeed.Enabled = true;
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
            if (bluetoothClient.Connected)
            {
                //Request data
                stream.WriteByte(PACKAGE_QUERY);

                //raw data count
                new_buffer_cnt = rawQueue.Count;

                if (new_buffer_cnt > 3)
                {
                    //decode new data
                    byte incoming_byte;
                    bool should_record = false;
                    byte[] raw_byte = new byte[64];
                    int raw_byte_ptr = 0;

                    for (int i = 0; i < new_buffer_cnt; i++)
                    {
                        incoming_byte = (byte)rawQueue.Dequeue();
                        if (should_record)
                        {
                            if (incoming_byte == PACKAGE_END)
                            {
                                //complete one new data
                                should_record = false;
                                string new_data_str = System.Text.Encoding.UTF8.GetString(raw_byte, 0, raw_byte_ptr);
                                string[] new_datas = new_data_str.Split(',');
                                Package new_package = new Package();

                                new_package.index = x_axis_cnt * zoom_ratio;
                                new_package.pitch = float.Parse(new_datas[0], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.intent = int.Parse(new_datas[1], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.is_boost = new_package.intent == 3 ? 1 : 0;
                                new_package.left_motor.voltage = float.Parse(new_datas[2], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.left_motor.current = float.Parse(new_datas[3], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.left_motor.rpm = int.Parse(new_datas[4], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.right_motor.voltage = float.Parse(new_datas[5], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.right_motor.current = float.Parse(new_datas[6], System.Globalization.CultureInfo.InvariantCulture);
                                new_package.right_motor.rpm = int.Parse(new_datas[7], System.Globalization.CultureInfo.InvariantCulture);

                                //Dequeue if too many data
                                if (x_axis_cnt > SAMPLES_PER_CHART)
                                {
                                    dataQueue.Dequeue();
                                }

                                //Enqueue new data
                                dataQueue.Enqueue(new_package);
                                x_axis_cnt++;

                                //reset rx buffer pointer for next package
                                raw_byte_ptr = 0;

                                //Add point to chart
                                this.chart1.Series[0].Points.Clear();
                                this.chart1.Series[1].Points.Clear();
                                this.chart2.Series[0].Points.Clear();
                                this.chart2.Series[1].Points.Clear();
                                this.chart3.Series[0].Points.Clear();
                                this.chart3.Series[1].Points.Clear();
                                this.chart4.Series[0].Points.Clear();
                                this.chart4.Series[1].Points.Clear();
                                this.chart5.Series[0].Points.Clear();
                                this.chart5.Series[1].Points.Clear();
                                this.chart6.Series[0].Points.Clear();
                                this.chart6.Series[1].Points.Clear();
                                this.chart7.Series[0].Points.Clear();
                                this.chart7.Series[1].Points.Clear();
                                this.chart8.Series[0].Points.Clear();
                                for (int idx = 0; idx < dataQueue.Count; idx++)
                                {
                                    this.chart1.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).pitch);
                                    this.chart1.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (PITCH_MAX - PITCH_MIN));
                                    this.chart2.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).left_motor.voltage);
                                    this.chart2.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (VOLTAGE_MAX - VOLTAGE_MIN));
                                    this.chart3.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).left_motor.current);
                                    this.chart3.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (CURRENT_MAX - CURRENT_MIN));
                                    this.chart4.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).left_motor.rpm);
                                    this.chart4.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (RPM_MAX - RPM_MIN));
                                    this.chart5.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).right_motor.voltage);
                                    this.chart5.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (VOLTAGE_MAX - VOLTAGE_MIN));
                                    this.chart6.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).right_motor.current);
                                    this.chart6.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (CURRENT_MAX - CURRENT_MIN));
                                    this.chart7.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).right_motor.rpm);
                                    this.chart7.Series[1].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).is_boost * (RPM_MAX - RPM_MIN));
                                    this.chart8.Series[0].Points.AddXY(dataQueue.ElementAt(idx).index, dataQueue.ElementAt(idx).intent);
                                }

                                //move chart x axis
                                if (x_axis_cnt > SAMPLES_PER_CHART)
                                {
                                    this.chart1.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart1.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart1.ChartAreas[0].AxisX.IntervalOffset = -this.chart1.ChartAreas[0].AxisX.Minimum % this.chart1.ChartAreas[0].AxisX.Interval;
                                    this.chart2.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart2.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart2.ChartAreas[0].AxisX.IntervalOffset = -this.chart2.ChartAreas[0].AxisX.Minimum % this.chart2.ChartAreas[0].AxisX.Interval;
                                    this.chart3.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart3.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart3.ChartAreas[0].AxisX.IntervalOffset = -this.chart3.ChartAreas[0].AxisX.Minimum % this.chart3.ChartAreas[0].AxisX.Interval;
                                    this.chart4.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart4.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart4.ChartAreas[0].AxisX.IntervalOffset = -this.chart4.ChartAreas[0].AxisX.Minimum % this.chart4.ChartAreas[0].AxisX.Interval;
                                    this.chart5.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart5.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart5.ChartAreas[0].AxisX.IntervalOffset = -this.chart5.ChartAreas[0].AxisX.Minimum % this.chart5.ChartAreas[0].AxisX.Interval;
                                    this.chart6.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart6.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart6.ChartAreas[0].AxisX.IntervalOffset = -this.chart6.ChartAreas[0].AxisX.Minimum % this.chart6.ChartAreas[0].AxisX.Interval;
                                    this.chart7.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart7.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart7.ChartAreas[0].AxisX.IntervalOffset = -this.chart7.ChartAreas[0].AxisX.Minimum % this.chart7.ChartAreas[0].AxisX.Interval;
                                    this.chart8.ChartAreas[0].AxisX.Minimum = (x_axis_cnt - SAMPLES_PER_CHART) * zoom_ratio;
                                    this.chart8.ChartAreas[0].AxisX.Maximum = x_axis_cnt * zoom_ratio;
                                    this.chart8.ChartAreas[0].AxisX.IntervalOffset = -this.chart8.ChartAreas[0].AxisX.Minimum % this.chart8.ChartAreas[0].AxisX.Interval;
                                }
                            }
                            else
                            {
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
        }

        private void InitChart()
        {
            zoom_ratio = (double.Parse(textBoxQuerySpeed.Text) / 1000);

            //Define chart area
            this.chart1.ChartAreas.Clear();
            ChartArea chartArea1 = new ChartArea("ChartArea1");
            this.chart1.ChartAreas.Add(chartArea1);

            this.chart2.ChartAreas.Clear();
            ChartArea chartArea2 = new ChartArea("ChartArea2");
            this.chart2.ChartAreas.Add(chartArea2);

            this.chart3.ChartAreas.Clear();
            ChartArea chartArea3 = new ChartArea("ChartArea3");
            this.chart3.ChartAreas.Add(chartArea3);

            this.chart4.ChartAreas.Clear();
            ChartArea chartArea4 = new ChartArea("ChartArea4");
            this.chart4.ChartAreas.Add(chartArea4);

            this.chart5.ChartAreas.Clear();
            ChartArea chartArea5 = new ChartArea("ChartArea5");
            this.chart5.ChartAreas.Add(chartArea5);

            this.chart6.ChartAreas.Clear();
            ChartArea chartArea6 = new ChartArea("ChartArea6");
            this.chart6.ChartAreas.Add(chartArea6);

            this.chart7.ChartAreas.Clear();
            ChartArea chartArea7 = new ChartArea("ChartArea7");
            this.chart7.ChartAreas.Add(chartArea7);

            this.chart8.ChartAreas.Clear();
            ChartArea chartArea8 = new ChartArea("ChartArea8");
            this.chart8.ChartAreas.Add(chartArea8);

            //Define chart series
            this.chart1.Series.Clear();
            Series series1 = new Series("Pitch");
            series1.ChartArea = "ChartArea1";
            this.chart1.Series.Add(series1);
            Series series8 = new Series("Boost");
            series8.ChartArea = "ChartArea1";
            this.chart1.Series.Add(series8);

            this.chart2.Series.Clear();
            Series series2 = new Series("Voltage_L");
            series2.ChartArea = "ChartArea2";
            this.chart2.Series.Add(series2);
            Series series9 = new Series("Boost_copy1");
            series9.ChartArea = "ChartArea2";
            this.chart2.Series.Add(series9);

            this.chart3.Series.Clear();
            Series series3 = new Series("Current_L");
            series3.ChartArea = "ChartArea3";
            this.chart3.Series.Add(series3);
            Series series10 = new Series("Boost_copy2");
            series10.ChartArea = "ChartArea3";
            this.chart3.Series.Add(series10);

            this.chart4.Series.Clear();
            Series series4 = new Series("RPM_L");
            series4.ChartArea = "ChartArea4";
            this.chart4.Series.Add(series4);
            Series series11 = new Series("Boost_copy3");
            series11.ChartArea = "ChartArea4";
            this.chart4.Series.Add(series11);

            this.chart5.Series.Clear();
            Series series5 = new Series("Voltage_R");
            series5.ChartArea = "ChartArea5";
            this.chart5.Series.Add(series5);
            Series series12 = new Series("Boost_copy4");
            series12.ChartArea = "ChartArea5";
            this.chart5.Series.Add(series12);

            this.chart6.Series.Clear();
            Series series6 = new Series("Current_R");
            series6.ChartArea = "ChartArea6";
            this.chart6.Series.Add(series6);
            Series series13 = new Series("Boost_copy5");
            series13.ChartArea = "ChartArea6";
            this.chart6.Series.Add(series13);

            this.chart7.Series.Clear();
            Series series7 = new Series("RPM_R");
            series7.ChartArea = "ChartArea7";
            this.chart7.Series.Add(series7);
            Series series14 = new Series("Boost_copy2");
            series14.ChartArea = "ChartArea7";
            this.chart7.Series.Add(series14);

            this.chart8.Series.Clear();
            Series series15 = new Series("Intent");
            series15.ChartArea = "ChartArea8";
            this.chart8.Series.Add(series15);

            //Set chartArea Apperence
            this.chart1.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart1.ChartAreas[0].AxisX.Minimum = 0;
            this.chart1.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart1.ChartAreas[0].AxisY.Minimum = PITCH_MIN;
            this.chart1.ChartAreas[0].AxisY.Maximum = PITCH_MAX;
            this.chart1.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart1.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart1.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart1.Legends.Clear();

            this.chart2.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart2.ChartAreas[0].AxisX.Minimum = 0;
            this.chart2.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart2.ChartAreas[0].AxisY.Minimum = VOLTAGE_MIN;
            this.chart2.ChartAreas[0].AxisY.Maximum = VOLTAGE_MAX;
            this.chart2.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart2.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart2.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart2.Legends.Clear();

            this.chart3.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart3.ChartAreas[0].AxisX.Minimum = 0;
            this.chart3.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart3.ChartAreas[0].AxisY.Minimum = CURRENT_MIN;
            this.chart3.ChartAreas[0].AxisY.Maximum = CURRENT_MAX;
            this.chart3.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart3.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart3.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart3.Legends.Clear();

            this.chart4.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart4.ChartAreas[0].AxisX.Minimum = 0;
            this.chart4.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart4.ChartAreas[0].AxisY.Minimum = RPM_MIN;
            this.chart4.ChartAreas[0].AxisY.Maximum = RPM_MAX;
            this.chart4.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart4.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart4.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart4.Legends.Clear();

            this.chart5.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart5.ChartAreas[0].AxisX.Minimum = 0;
            this.chart5.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart5.ChartAreas[0].AxisY.Minimum = VOLTAGE_MIN;
            this.chart5.ChartAreas[0].AxisY.Maximum = VOLTAGE_MAX;
            this.chart5.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart5.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart5.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart5.Legends.Clear();

            this.chart6.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart6.ChartAreas[0].AxisX.Minimum = 0;
            this.chart6.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart6.ChartAreas[0].AxisY.Minimum = CURRENT_MIN;
            this.chart6.ChartAreas[0].AxisY.Maximum = CURRENT_MAX;
            this.chart6.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart6.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart6.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart6.Legends.Clear();

            this.chart7.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart7.ChartAreas[0].AxisX.Minimum = 0;
            this.chart7.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart7.ChartAreas[0].AxisY.Minimum = RPM_MIN;
            this.chart7.ChartAreas[0].AxisY.Maximum = RPM_MAX;
            this.chart7.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart7.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart7.ChartAreas[0].InnerPlotPosition = new ElementPosition(10, 0, 90, 85);
            this.chart7.Legends.Clear();

            this.chart8.ChartAreas[0].AxisX.Interval = AXIS_X_INTERVAL;
            this.chart8.ChartAreas[0].AxisX.Minimum = 0;
            this.chart8.ChartAreas[0].AxisX.Maximum = SAMPLES_PER_CHART * zoom_ratio;
            this.chart8.ChartAreas[0].AxisY.Minimum = INTENT_MIN;
            this.chart8.ChartAreas[0].AxisY.Maximum = INTENT_MAX;
            this.chart8.ChartAreas[0].AxisX.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart8.ChartAreas[0].AxisY.MajorGrid.LineColor = System.Drawing.Color.Silver;
            this.chart8.ChartAreas[0].InnerPlotPosition = new ElementPosition(20, 0, 80, 85);
            this.chart8.ChartAreas[0].AxisY.CustomLabels.Add(-0.5, 0.5, "STOP");
            this.chart8.ChartAreas[0].AxisY.CustomLabels.Add(0.5, 1.5, "Forward");
            this.chart8.ChartAreas[0].AxisY.CustomLabels.Add(1.5, 2.5, "Backward");
            this.chart8.ChartAreas[0].AxisY.CustomLabels.Add(2.5, 3.5, "Boost");
            this.chart8.ChartAreas[0].AxisY.CustomLabels.Add(3.5, 4.5, "TURN");
            this.chart8.Legends.Clear();

            //Set chart title
            this.chart1.Titles.Clear();
            this.chart1.Titles.Add("Title01");
            this.chart1.Titles[0].Text = "Pitch";
            this.chart1.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart1.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart2.Titles.Clear();
            this.chart2.Titles.Add("Title02");
            this.chart2.Titles[0].Text = "Motor Left Voltage";
            this.chart2.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart2.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart3.Titles.Clear();
            this.chart3.Titles.Add("Title03");
            this.chart3.Titles[0].Text = "Motor Left Current";
            this.chart3.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart3.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart4.Titles.Clear();
            this.chart4.Titles.Add("Title04");
            this.chart4.Titles[0].Text = "Motor Left RPM";
            this.chart4.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart4.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart5.Titles.Clear();
            this.chart5.Titles.Add("Title05");
            this.chart5.Titles[0].Text = "Motor Right Voltage";
            this.chart5.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart5.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart6.Titles.Clear();
            this.chart6.Titles.Add("Title06");
            this.chart6.Titles[0].Text = "Motor Right Current";
            this.chart6.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart6.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart7.Titles.Clear();
            this.chart7.Titles.Add("Title07");
            this.chart7.Titles[0].Text = "Motor Right RPM";
            this.chart7.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart7.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            this.chart8.Titles.Clear();
            this.chart8.Titles.Add("Title08");
            this.chart8.Titles[0].Text = "Intent";
            this.chart8.Titles[0].ForeColor = Color.RoyalBlue;
            this.chart8.Titles[0].Font = new System.Drawing.Font("Microsoft Sans Serif", 12F);

            //Set Chart Apperence
            series1.ChartType = SeriesChartType.Line;
            series1.BorderWidth = 2;
            series1.Color = Color.FromArgb(100, Color.Maroon);
            series1.Points.Clear();

            series2.ChartType = SeriesChartType.Line;
            series2.BorderWidth = 2;
            series2.Color = Color.FromArgb(100, Color.Red);
            series2.Points.Clear();

            series3.ChartType = SeriesChartType.Line;
            series3.BorderWidth = 2;
            series3.Color = Color.FromArgb(100, Color.Blue);
            series3.Points.Clear();

            series4.ChartType = SeriesChartType.Line;
            series4.BorderWidth = 2;
            series4.Color = Color.FromArgb(100, Color.Green);
            series4.Points.Clear();

            series5.ChartType = SeriesChartType.Line;
            series5.BorderWidth = 2;
            series5.Color = Color.FromArgb(100, Color.Red);
            series5.Points.Clear();

            series6.ChartType = SeriesChartType.Line;
            series6.BorderWidth = 2;
            series6.Color = Color.FromArgb(100, Color.Blue);
            series6.Points.Clear();

            series7.ChartType = SeriesChartType.Line;
            series7.BorderWidth = 2;
            series7.Color = Color.FromArgb(100, Color.Green);
            series7.Points.Clear();

            series8.ChartType = SeriesChartType.Area;
            series8.Color = Color.FromArgb(60, Color.DarkOrange);
            series8.Points.Clear();

            series9.ChartType = SeriesChartType.Area;
            series9.Color = Color.FromArgb(60, Color.DarkOrange);
            series9.Points.Clear();

            series10.ChartType = SeriesChartType.Area;
            series10.Color = Color.FromArgb(60, Color.DarkOrange);
            series10.Points.Clear();

            series11.ChartType = SeriesChartType.Area;
            series11.Color = Color.FromArgb(60, Color.DarkOrange);
            series11.Points.Clear();

            series12.ChartType = SeriesChartType.Area;
            series12.Color = Color.FromArgb(60, Color.DarkOrange);
            series12.Points.Clear();

            series13.ChartType = SeriesChartType.Area;
            series13.Color = Color.FromArgb(60, Color.DarkOrange);
            series13.Points.Clear();

            series14.ChartType = SeriesChartType.Area;
            series14.Color = Color.FromArgb(60, Color.DarkOrange);
            series14.Points.Clear();

            series15.ChartType = SeriesChartType.Line;
            series15.BorderWidth = 2;
            series15.Color = Color.FromArgb(60, Color.Red);
            series15.Points.Clear();
        }

        //show tooltip when mouse close to data
        private void chart1_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart1.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart1,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart2_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart2.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart2,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart3_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart3.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart3,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart4_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart4.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart4,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart5_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart5.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart5,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart6_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart6.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart6,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void chart7_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.Location;
            if (prevPosition.HasValue && pos == prevPosition.Value)
                return;
            tooltip.RemoveAll();
            prevPosition = pos;
            var results = chart7.HitTest(pos.X, pos.Y, false,
                                            ChartElementType.DataPoint);
            foreach (var result in results)
            {
                if (result.ChartElementType == ChartElementType.DataPoint)
                {
                    var prop = result.Object as DataPoint;
                    if (prop != null)
                    {
                        var pointXPixel = result.ChartArea.AxisX.ValueToPixelPosition(prop.XValue);
                        var pointYPixel = result.ChartArea.AxisY.ValueToPixelPosition(prop.YValues[0]);

                        // check if the cursor is really close to the point (2 pixels around the point)
                        if (Math.Abs(pos.X - pointXPixel) < 2 &&
                            Math.Abs(pos.Y - pointYPixel) < 2)
                        {
                            tooltip.Show("X=" + prop.XValue + ", Y=" + prop.YValues[0], this.chart7,
                                            pos.X, pos.Y - 15);
                        }
                    }
                }
            }
        }

        private void btn_reset_Click(object sender, EventArgs e)
        {
            this.chart1.Series[0].Points.Clear();
            this.chart1.Series[1].Points.Clear();
            this.chart2.Series[0].Points.Clear();
            this.chart2.Series[1].Points.Clear();
            this.chart3.Series[0].Points.Clear();
            this.chart3.Series[1].Points.Clear();
            this.chart4.Series[0].Points.Clear();
            this.chart4.Series[1].Points.Clear();
            this.chart5.Series[0].Points.Clear();
            this.chart5.Series[1].Points.Clear();
            this.chart6.Series[0].Points.Clear();
            this.chart6.Series[1].Points.Clear();
            this.chart7.Series[0].Points.Clear();
            this.chart7.Series[1].Points.Clear();
            this.chart8.Series[0].Points.Clear();

            this.dataQueue.Clear();

            x_axis_cnt = 0;
        }
    }
}
