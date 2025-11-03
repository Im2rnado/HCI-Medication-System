using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.IO;
using System.Text.Json;

namespace TuioPatientUI
{
    public partial class MainWindow : Window
    {
        private const string TCP_HOST = "127.0.0.1";
        private const int TCP_PORT = 8765;

        // Enhanced medication list
        private readonly List<string> MedNames = new List<string>
        {
            "Paracetamol", "Amoxicillin", "Aspirin", "Metformin",
            "Lisinopril", "Atorvastatin"
        };

        // Patient list (currently one patient, but infrastructure for multiple)
        private readonly List<string> PatientNames = new List<string>
        {
            "Patient 1"
        };

        private readonly List<Button> _sectorButtons = new List<Button>();
        private string _currentPatient = "Patient 1"; // Default patient
        private bool _isNurseWheelActive = false;
        private enum NurseWheelType { PatientSelection, MedicationEdit }
        private NurseWheelType _currentNurseWheelType = NurseWheelType.PatientSelection;
        
        // Debouncing for alerts
        private bool _isShowingAlert = false;
        private DateTime _lastAlertTime = DateTime.MinValue;
        
        // Warning panel auto-hide
        private System.Windows.Threading.DispatcherTimer _warningTimer;
        private int _warningSecondsRemaining;

        private System.Windows.Threading.DispatcherTimer _successTimer;
        private int _successSecondsRemaining;

        // Enhanced state machine
        private enum Mode { Idle, SelectingMedication, ConfirmingTaken, NurseMode, NurseViewInfo, NurseEditMeds, NurseEditingTime }
        private Mode _mode = Mode.Idle;
        private int _hoveredSector = -1;    
        private string _hoveredMedication = ""; // Store hovered medication for gesture editing
        private int _selectedMedIndex = -1;
        private string _selectedMedication = "";

        // Medication tracking
        private List<MedicationRecord> _medicationHistory = new List<MedicationRecord>();
        private const int HOURS_BETWEEN_DOSES = 12;
        private ExcelDataManager _excelManager = new ExcelDataManager();
        private List<ExcelDataManager.MedicationSchedule> _medicationSchedules = new List<ExcelDataManager.MedicationSchedule>();

        // Networking
        private TcpClient _client;
        private NetworkStream _stream;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            SetupWheel();
            UpdateInstruction();
            LoadMedicationHistory();
        }

        // Medication record class
        public class MedicationRecord
        {
            public string MedicationName { get; set; }
            public DateTime TimeTaken { get; set; }
            public DateTime NextDoseTime { get; set; }
            public bool Taken { get; set; }
            public List<TimeSpan> ScheduledTimes { get; set; } = new List<TimeSpan>();
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() =>
            {
                LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {s}");
                if (LogList.Items.Count > 200) LogList.Items.RemoveAt(LogList.Items.Count - 1);
                System.Diagnostics.Debug.WriteLine($"[UI] {s}");
            });
        }

        private bool ShowAlertWithCooldown(string message, string title, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            // Check if enough time has passed since last alert
            if ((DateTime.Now - _lastAlertTime).TotalMilliseconds < 2000)
            {
                return false; // Skip showing alert
            }

            if (_isShowingAlert)
            {
                return false; // Already showing an alert
            }

            _isShowingAlert = true;
            _lastAlertTime = DateTime.Now;

            try
            {
                var result = MessageBox.Show(message, title, button, icon);
                return result == MessageBoxResult.OK || result == MessageBoxResult.Yes;
            }
            finally
            {
                _isShowingAlert = false;
            }
        }

        private void SetMode(Mode m)
        {
            _mode = m;
            Dispatcher.Invoke(() =>
            {
                ModeText.Text = $"Mode: {m}";
                UpdateInstruction();
            });
        }

        private void UpdateInstruction()
        {
            Dispatcher.Invoke(() =>
            {
                switch (_mode)
                {
                    case Mode.Idle:
                        InstructionText.Text = "Place ROTATE marker (0) to select medication OR place NURSE marker (13) for nurse mode";
                        break;
                    case Mode.SelectingMedication:
                        InstructionText.Text = "Rotate marker to choose medication, then place SELECT marker nearby to take it";
                        break;
                    case Mode.NurseMode:
                        if (_isNurseWheelActive && _currentNurseWheelType == NurseWheelType.PatientSelection)
                            InstructionText.Text = "NURSE: Rotate 13 to select patient, place 14 near 13 to view info";
                        else
                            InstructionText.Text = "NURSE: Place 13+14 to view patient, or 13+15 to edit medications";
                        break;
                    case Mode.NurseViewInfo:
                        InstructionText.Text = "Viewing patient info. Press 12 to go back to wheel, or 13+15 to edit meds";
                        break;
                    case Mode.NurseEditMeds:
                        InstructionText.Text = "Rotate 13 to hover on medication, then place marker 15 to enter gesture mode and edit time";
                        break;
                    case Mode.NurseEditingTime:
                        InstructionText.Text = "Move hand LEFT to decrease time, RIGHT to increase time. Close palm to save.";
                        break;
                }
            });
        }

        #region Medication History Management

        private void LoadMedicationHistory()
        {
            try
            {
                // Load medication schedules from Excel
                _medicationSchedules = _excelManager.LoadMedicationSchedule();
                Log($"Loaded {_medicationSchedules.Count} medication schedules");
                
                // Populate medication schedule display
                PopulateMedicationScheduleDisplay();

                // Load history from Excel
                var excelHistory = _excelManager.LoadHistory();
                _medicationHistory = excelHistory.Select(eh => new MedicationRecord
                {
                    MedicationName = eh.MedicationName,
                    TimeTaken = eh.TimeTaken,
                    NextDoseTime = eh.NextDoseTime,
                    Taken = eh.Taken,
                    ScheduledTimes = _medicationSchedules
                        .FirstOrDefault(s => s.MedicationName == eh.MedicationName)?.DoseTimes ?? new List<TimeSpan>()
                }).ToList();

                Log($"Loaded {_medicationHistory.Count} medication records from Excel");
                UpdateHistoryDisplay();
            }
            catch (Exception ex)
            {
                Log($"Error loading history: {ex.Message}");
                _medicationHistory = new List<MedicationRecord>();
            }
        }

        private void UpdateHistoryDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                HistoryList.Items.Clear();

                // Show latest 10 records
                var recentRecords = _medicationHistory
                    .OrderByDescending(r => r.TimeTaken)
                    .Take(10);

                foreach (var record in recentRecords)
                {
                    string status = record.Taken ? "TAKEN" : "NOT TAKEN";
                    string nextDoseInfo = record.Taken ?
                        $"Next: {record.NextDoseTime:HH:mm}" :
                        "Not taken";

                    HistoryList.Items.Add($"{record.MedicationName} - {record.TimeTaken:HH:mm} - {status} - {nextDoseInfo}");
                }

                // Update next dose information
                UpdateNextDoseInfo();
            });
        }

        private void PopulateMedicationScheduleDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                SchedulePanel.Children.Clear();

                if (_medicationSchedules == null || _medicationSchedules.Count == 0)
                {
                    var noScheduleText = new TextBlock
                    {
                        Text = "No medication schedules available",
                        FontSize = 12,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(5),
                        TextAlignment = TextAlignment.Center
                    };
                    SchedulePanel.Children.Add(noScheduleText);
                    return;
                }

                foreach (var schedule in _medicationSchedules.Where(s => s.Enabled).OrderBy(s => s.MedicationName))
                {
                    // Create a border for each medication
                    var medBorder = new Border
                    {
                        BorderBrush = Brushes.LightBlue,
                        BorderThickness = new Thickness(1),
                        Background = Brushes.AliceBlue,
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(10),
                        CornerRadius = new CornerRadius(5)
                    };

                    var medStack = new StackPanel();

                    // Medication name
                    var nameText = new TextBlock
                    {
                        Text = schedule.MedicationName,
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.DarkBlue,
                        Margin = new Thickness(0, 0, 0, 5)
                    };
                    medStack.Children.Add(nameText);

                    // Scheduled times
                    if (schedule.DoseTimes != null && schedule.DoseTimes.Any())
                    {
                        var timesText = new TextBlock
                        {
                            Text = "📅 " + string.Join(", ", schedule.DoseTimes.Select(t => t.ToString(@"hh\:mm"))),
                            FontSize = 12,
                            Foreground = Brushes.DarkGreen,
                            TextWrapping = TextWrapping.Wrap
                        };
                        medStack.Children.Add(timesText);

                        // Next dose indicator
                        var now = DateTime.Now.TimeOfDay;
                        var nextDose = schedule.DoseTimes
                            .Where(t => t > now)
                            .OrderBy(t => t)
                            .FirstOrDefault();

                        if (nextDose != TimeSpan.Zero)
                        {
                            var nextDoseText = new TextBlock
                            {
                                Text = $"⏰ Next: {nextDose:hh\\:mm}",
                                FontSize = 11,
                                FontStyle = FontStyles.Italic,
                                Foreground = Brushes.DarkOrange,
                                Margin = new Thickness(0, 3, 0, 0)
                            };
                            medStack.Children.Add(nextDoseText);
                        }
                        else
                        {
                            // If no upcoming dose today, show first dose of tomorrow
                            var firstDose = schedule.DoseTimes.OrderBy(t => t).FirstOrDefault();
                            if (firstDose != TimeSpan.Zero)
                            {
                                var nextDoseText = new TextBlock
                                {
                                    Text = $"⏰ Next: {firstDose:hh\\:mm} (tomorrow)",
                                    FontSize = 11,
                                    FontStyle = FontStyles.Italic,
                                    Foreground = Brushes.DarkOrange,
                                    Margin = new Thickness(0, 3, 0, 0)
                                };
                                medStack.Children.Add(nextDoseText);
                            }
                        }
                    }
                    else
                    {
                        var noTimesText = new TextBlock
                        {
                            Text = "No scheduled times",
                            FontSize = 11,
                            Foreground = Brushes.Gray,
                            FontStyle = FontStyles.Italic
                        };
                        medStack.Children.Add(noTimesText);
                    }

                    medBorder.Child = medStack;
                    SchedulePanel.Children.Add(medBorder);
                }

                if (!_medicationSchedules.Any(s => s.Enabled))
                {
                    var noEnabledText = new TextBlock
                    {
                        Text = "All medications are currently disabled",
                        FontSize = 12,
                        Foreground = Brushes.Orange,
                        Margin = new Thickness(5),
                        TextAlignment = TextAlignment.Center
                    };
                    SchedulePanel.Children.Add(noEnabledText);
                }
            });
        }

        private void UpdateNextDoseInfo()
        {
            var upcomingDoses = _medicationHistory
                .Where(r => r.Taken && r.NextDoseTime > DateTime.Now)
                .OrderBy(r => r.NextDoseTime)
                .ToList();

            if (upcomingDoses.Any())
            {
                var nextDose = upcomingDoses.First();
                TimeSpan timeUntilNext = nextDose.NextDoseTime - DateTime.Now;
                string timeString = timeUntilNext.TotalHours >= 1 ?
                    $"{timeUntilNext.TotalHours:F1} hours" :
                    $"{timeUntilNext.TotalMinutes:F0} minutes";

                NextDoseText.Text = $"Next dose: {nextDose.MedicationName} in {timeString}";
                NextDoseText.Foreground = timeUntilNext.TotalHours < 1 ? Brushes.Red : Brushes.Green;
            }
            else
            {
                NextDoseText.Text = "No upcoming doses";
                NextDoseText.Foreground = Brushes.Gray;
            }
        }

        private void AddMedicationRecord(string medication, bool taken)
        {
            var record = new MedicationRecord
            {
                MedicationName = medication,
                TimeTaken = DateTime.Now,
                NextDoseTime = DateTime.Now.AddHours(HOURS_BETWEEN_DOSES),
                Taken = taken,
                ScheduledTimes = _medicationSchedules
                    .FirstOrDefault(s => s.MedicationName == medication)?.DoseTimes ?? new List<TimeSpan>()
            };

            _medicationHistory.Add(record);
            
            // Save to Excel
            var excelRecord = new ExcelDataManager.MedicationHistoryRecord
            {
                MedicationName = medication,
                TimeTaken = DateTime.Now,
                ScheduledTime = DateTime.Now.TimeOfDay, // Current time as scheduled time
                Taken = taken,
                NextDoseTime = DateTime.Now.AddHours(HOURS_BETWEEN_DOSES)
            };
            _excelManager.AppendHistory(excelRecord);
            
            UpdateHistoryDisplay();

            if (taken)
            {
                Log($"Recorded: {medication} taken at {DateTime.Now:HH:mm}. Next dose at {record.NextDoseTime:HH:mm}");
            }
            else
            {
                Log($"Recorded: {medication} not taken at {DateTime.Now:HH:mm}");
            }
        }

        private bool CanTakeMedication(string medicationName, out string warningMessage)
        {
            warningMessage = "";
            const int GRACE_PERIOD_MINUTES = 10;

            // Find the most recent record for this medication where it was marked as taken
            var lastTakenRecord = _medicationHistory
                .Where(r => r.MedicationName == medicationName && r.Taken)
                .OrderByDescending(r => r.TimeTaken)
                .FirstOrDefault();

            if (lastTakenRecord == null)
            {
                // First time taking this medication
                return true;
            }

            // Check if trying to take before the next scheduled dose time (with 10-minute grace period)
            DateTime earliestAllowedTime = lastTakenRecord.NextDoseTime.AddMinutes(-GRACE_PERIOD_MINUTES);
            
            if (DateTime.Now < earliestAllowedTime)
            {
                TimeSpan timeRemaining = earliestAllowedTime - DateTime.Now;
                string timeString = timeRemaining.TotalHours >= 1 ?
                    $"{timeRemaining.TotalHours:F1} hours" :
                    $"{timeRemaining.TotalMinutes:F0} minutes";

                warningMessage = $"WARNING: It's too early to take {medicationName}!\n\n" +
                    $"Last taken: {lastTakenRecord.TimeTaken:HH:mm}\n" +
                    $"Next dose: {lastTakenRecord.NextDoseTime:HH:mm}\n" +
                    $"Time remaining: {timeString}\n\n" +
                    $"Please wait until {earliestAllowedTime:HH:mm} (10 minutes before scheduled dose).";
                return false;
            }

            return true;
        }

        #endregion

        #region Wheel UI

        private void SetupWheel()
        {
            double cx = WheelCanvas.Width / 2;
            double cy = WheelCanvas.Height / 2;
            double radius = 150;
            int n = MedNames.Count;

            WheelCanvas.Children.Clear();
            _sectorButtons.Clear();

            for (int i = 0; i < n; i++)
            {
                double angle = (2 * Math.PI * i) / n - Math.PI / 2;
                double bx = cx + Math.Cos(angle) * radius;
                double by = cy + Math.Sin(angle) * radius;

                var b = new Button()
                {
                    Width = 120,
                    Height = 40,
                    Content = MedNames[i],
                    Tag = i,
                    FontSize = 10
                };
                Canvas.SetLeft(b, bx - b.Width / 2);
                Canvas.SetTop(b, by - b.Height / 2);
                b.IsHitTestVisible = false;
                WheelCanvas.Children.Add(b);
                _sectorButtons.Add(b);
            }

            // Add center circle
            var centerEllipse = new Ellipse()
            {
                Width = 80,
                Height = 80,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 2
            };
            Canvas.SetLeft(centerEllipse, cx - 40);
            Canvas.SetTop(centerEllipse, cy - 40);
            WheelCanvas.Children.Add(centerEllipse);
        }

        private void ShowWheel(bool show)
        {
            Dispatcher.Invoke(() =>
            {
                WheelCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show)
                {
                    Popup.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void HighlightSector(int sectorIndex)
        {
            Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < _sectorButtons.Count; i++)
                {
                    var b = _sectorButtons[i];
                    // Normal wheel highlighting
                    b.Background = (i == sectorIndex) ? Brushes.LightSkyBlue : SystemColors.ControlBrush;
                    b.BorderBrush = (i == sectorIndex) ? Brushes.DodgerBlue : Brushes.Transparent;
                }
            });
            _hoveredSector = sectorIndex;
        }

        #endregion

        #region Networking
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Log("Starting TCP client...");
            Task.Run(async () => await StartTcpLoop());
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _stream?.Close();
                _client?.Close();
            }
            catch { }
        }

        private async Task StartTcpLoop()
        {
            while (true)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(TCP_HOST, TCP_PORT);
                    _stream = _client.GetStream();
                    SetStatus($"Connected to {TCP_HOST}:{TCP_PORT}");
                    Log("Connected to TUIO broadcaster");
                    await ReadLoop(_stream);
                }
                catch (Exception ex)
                {
                    SetStatus($"Disconnected - retrying in 2s ({ex.Message})");
                    Log("Connection failed: " + ex.Message);
                    await Task.Delay(2000);
                }
            }
        }

        private void SetStatus(string s)
        {
            Dispatcher.Invoke(() => StatusText.Text = s);
        }

        private async Task ReadLoop(NetworkStream ns)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            while (_client?.Connected == true)
            {
                int read = 0;
                try
                {
                    read = await ns.ReadAsync(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

                string s = sb.ToString();
                int idx;
                while ((idx = s.IndexOf('\n')) >= 0)
                {
                    string line = s.Substring(0, idx).Trim();
                    if (!string.IsNullOrEmpty(line))
                    {
                        try
                        {
                            ProcessJsonLine(line);
                        }
                        catch (Exception ex)
                        {
                            Log($"✗ JSON parse error: {ex.Message} | Line: {line.Substring(0, Math.Min(100, line.Length))}");
                        }
                    }
                    s = s.Substring(idx + 1);
                }
                sb.Clear();
                sb.Append(s);
            }

            Log("Disconnected from broadcaster");
            SetStatus("Disconnected");
            _stream?.Close();
            _client?.Close();
        }
        #endregion

        #region Message Processing & State Machine
        private void ProcessJsonLine(string line)
        {
            JObject j;
            string type;
            
            try
            {
                j = JObject.Parse(line);
                type = (string)j["type"];
            }
            catch (Exception ex)
            {
                Log($"✗ ERROR parsing JSON: {ex.Message} | Line: {line}");
                return;
            }

            switch (type)
            {
                case "wheel_open":
                    // Reset nurse mode flags and setup patient medication wheel
                    _isNurseWheelActive = false;
                    _currentNurseWheelType = NurseWheelType.PatientSelection;
                    _hoveredMedication = ""; // Clear hovered medication
                    
                    // Reload medication schedules from Excel to get latest changes
                    _medicationSchedules = _excelManager.LoadMedicationSchedule();
                    PopulateMedicationScheduleDisplay();
                    
                    SetMode(Mode.SelectingMedication);
                    Dispatcher.Invoke(() =>
                    {
                        SetupWheel(); // Reload patient medications
                        WheelCanvas.Visibility = Visibility.Visible;
                    });
                    break;

                case "wheel_hover":
                    int sector = j["sector"]?.Value<int>() ?? -1;
                    string medication = j["medication"]?.Value<string>();
                    if (sector >= 0)
                    {
                        HighlightSector(sector);
                        // Show which medication is being hovered
                        Dispatcher.Invoke(() => SelectedMedText.Text = $"Hovering: {medication}");
                    }
                    break;

                case "wheel_select_confirm":
                    int chosenSector = j["sector"]?.Value<int>() ?? -1;
                    string chosenMed = j["medication"]?.Value<string>();
                    Dispatcher.Invoke(() => HandleWheelSelect(chosenSector, chosenMed));
                    break;

                case "medication_selected":
                    // Direct medication marker detection
                    string directMed = j["medication"]?.Value<string>();
                    int symbolId = j["symbol_id"]?.Value<int>() ?? -1;
                    Log($"Direct medication selection: {directMed} (symbol {symbolId})");
                    Dispatcher.Invoke(() => HandleDirectMedicationSelect(directMed));
                    break;

                case "back_pressed":
                    Log("Back pressed - canceling current operation");
                    HandleBackPressed();
                    break;

                case "nurse_wheel_open":
                    Log("Nurse wheel opened (marker 13)");
                    Dispatcher.Invoke(() => HandleNurseWheelOpen());
                    break;

                case "nurse_wheel_hover":
                    int nurseSector = j["sector"]?.Value<int>() ?? -1;
                    string nurseItem = j["medication"]?.Value<string>();
                    if (nurseSector >= 0)
                    {
                        Dispatcher.Invoke(() => HandleNurseWheelHover(nurseSector, nurseItem));
                    }
                    break;

                case "nurse_wheel_select_confirm":
                    int nurseChosenSector = j["sector"]?.Value<int>() ?? -1;
                    string nurseChosenItem = j["item"]?.Value<string>();
                    Dispatcher.Invoke(() => HandleNurseWheelSelect(nurseChosenSector, nurseChosenItem));
                    break;

                case "nurse_edit_med_select":
                    int editMedSector = j["sector"]?.Value<int>() ?? -1;
                    string editMedName = j["medication"]?.Value<string>();
                    Dispatcher.Invoke(() => HandleEditMedicationMode(editMedSector, editMedName));
                    break;

                case "gesture_mode_toggled":
                    bool gestureEnabled = j["enabled"]?.Value<bool>() ?? false;
                    Dispatcher.Invoke(() => HandleGestureModeToggle(gestureEnabled));
                    break;

                case "gesture_swipe":
                    string direction = j["direction"]?.Value<string>();
                    Dispatcher.Invoke(() => HandleGestureSwipe(direction));
                    break;

                case "gesture_time_update":
                    string timeUpdate = j["time"]?.Value<string>();
                    int minutesUpdate = j["minutes"]?.Value<int>() ?? 0;
                    Dispatcher.Invoke(() => HandleGestureTimeUpdate(timeUpdate, minutesUpdate));
                    break;

                case "gesture_time_final":
                    string timeFinal = j["time"]?.Value<string>();
                    int minutesFinal = j["minutes"]?.Value<int>() ?? 0;
                    Dispatcher.Invoke(() => HandleGestureTimeFinal(timeFinal, minutesFinal));
                    break;

                default:
                    break;
            }
        }

        private void HandleWheelSelect(int sector, string medication)
        {
            if (_mode == Mode.SelectingMedication)
            {
                // Mark as taken
                _selectedMedication = medication;
                _selectedMedIndex = sector;
                SelectedMedText.Text = $"Selected: {medication}";
                MarkConfirmation();
            }
            else if (_mode == Mode.NurseEditMeds)
            {
                // In nurse edit mode, select medication for editing
                _selectedMedication = medication;
                _selectedMedIndex = sector;
                
                Dispatcher.Invoke(() => { SelectedMedText.Text = $"Selected for editing: {medication}. Show marker 15 to enter gesture mode."; });
            }
        }

        private void HandleDirectMedicationSelect(string medication)
        {
            if (_mode == Mode.Idle || _mode == Mode.SelectingMedication)
            {
                _selectedMedication = medication;
                SelectedMedText.Text = $"Selected: {medication}";
                ShowWheel(false);
                MarkConfirmation();
            }
        }

        private DateTime? GetNextScheduledDoseTime(string medicationName)
        {
            // Find the medication schedule
            var schedule = _medicationSchedules.FirstOrDefault(s => s.MedicationName == medicationName && s.Enabled);
            if (schedule == null || schedule.DoseTimes == null || schedule.DoseTimes.Count == 0)
            {
                return null; // No schedule found
            }

            DateTime now = DateTime.Now;
            TimeSpan currentTime = now.TimeOfDay;

            // Find the next scheduled dose time (today or tomorrow)
            var dosesToday = schedule.DoseTimes
                .Where(t => t > currentTime)
                .OrderBy(t => t)
                .ToList();

            if (dosesToday.Count > 0)
            {
                // Next dose is today
                return now.Date.Add(dosesToday[0]);
            }
            else
            {
                // Next dose is tomorrow (first dose of the day)
                TimeSpan firstDose = schedule.DoseTimes.OrderBy(t => t).First();
                return now.Date.AddDays(1).Add(firstDose);
            }
        }

        private void MarkConfirmation()
        {
            Log($"Medication confirmation: {_selectedMedication} => TAKEN");

            // Check if medication can be taken (duplicate dosage protection)
            if (!CanTakeMedication(_selectedMedication, out string warningMessage))
            {
                // Show warning panel and prevent taking medication too early (no mouse needed!)
                ShowWarningPanel(warningMessage);
                Log($"Duplicate dosage prevented for {_selectedMedication}");
                return; // Don't record the medication
            }

            // Add to medication history
            AddMedicationRecord(_selectedMedication, true);

            Dispatcher.Invoke(() =>
            {
                DateTime now = DateTime.Now;
                DateTime? nextDose = GetNextScheduledDoseTime(_selectedMedication);

                if (nextDose.HasValue)
                {
                    TimeSpan timeUntilNext = nextDose.Value - now;
                    string timeUntilText = timeUntilNext.Hours > 0 
                        ? $"{timeUntilNext.Hours} hours {timeUntilNext.Minutes} minutes"
                        : $"{timeUntilNext.Minutes} minutes";

                    SelectedMedText.Text = $"{_selectedMedication} taken at {now:HH:mm}. Next dose at {nextDose.Value:HH:mm}";

                    // Show success panel with next dose time
                    string successMessage = $"{_selectedMedication} recorded as taken at {now:HH:mm}\n\n" +
                        $"Next dose: {nextDose.Value:HH:mm} (in {timeUntilText})";
                    ShowSuccessPanel(successMessage);
                }
                else
                {
                    // Fallback if no schedule found
                    SelectedMedText.Text = $"{_selectedMedication} taken at {now:HH:mm}";
                    ShowSuccessPanel($"{_selectedMedication} recorded as taken at {now:HH:mm}");
                }

                ShowWheel(false);
                SetMode(Mode.Idle);
            });

            // Reset selection
            _selectedMedication = "";
            _selectedMedIndex = -1;
        }

        private void HandleBackPressed()
        {
            switch (_mode)
            {
                case Mode.NurseViewInfo:
                case Mode.NurseEditMeds:
                case Mode.NurseEditingTime:
                    // Return to nurse mode and show patient wheel again
                    SetMode(Mode.NurseMode);
                    _isNurseWheelActive = true;
                    _currentNurseWheelType = NurseWheelType.PatientSelection;
                    
                    // Hide all overlays first
                    HidePatientInfoPanel();
                    HideWarningPanel();
                    HideSuccessPanel();
                    
                    // Setup and show wheel
                    Dispatcher.Invoke(() =>
                    {
                        SetupWheelForPatientSelection();
                        WheelCanvas.Visibility = Visibility.Visible;
                        SelectedMedText.Text = "Back to patient selection";
                    });
                    break;
                case Mode.NurseMode:
                    // Exit nurse mode completely
                    SetMode(Mode.Idle);
                    ShowWheel(false);
                    _isNurseWheelActive = false;
                    _currentPatient = "Patient 1"; // Reset to default
                    Dispatcher.Invoke(() => SelectedMedText.Text = "Nurse mode exited");
                    break;
                default:
                    // Standard back behavior for patient mode
                    SetMode(Mode.Idle);
                    ShowWheel(false);
                    Dispatcher.Invoke(() => SelectedMedText.Text = "Selection canceled");
                    break;
            }
        }

        private void HandleNurseWheelOpen()
        {
            // First time marker 13 detected - enter nurse mode and show patient selection wheel
            if (_mode == Mode.Idle)
            {
                SetMode(Mode.NurseMode);
                _isNurseWheelActive = true;
                _currentNurseWheelType = NurseWheelType.PatientSelection;
                SetupWheelForPatientSelection();
                ShowWheel(true);
                SelectedMedText.Text = "SELECT PATIENT (Marker 13 to rotate, Marker 14 to select)";
            }
            else if (_mode == Mode.NurseMode && !_isNurseWheelActive)
            {
                // Nurse wants to select a different patient or edit medications
                _isNurseWheelActive = true;
                _currentNurseWheelType = NurseWheelType.PatientSelection;
                SetupWheelForPatientSelection();
                ShowWheel(true);
                SelectedMedText.Text = "SELECT PATIENT (Marker 13 to rotate, Marker 14 to select)";
            }
        }

        private void HandleNurseWheelHover(int sector, string item)
        {
            if (!_isNurseWheelActive && _mode != Mode.NurseEditMeds)
                return;

            HighlightSector(sector);

            if (_currentNurseWheelType == NurseWheelType.PatientSelection && _mode == Mode.NurseMode)
            {
                // Hovering over patient
                string patientName = PatientNames[sector % PatientNames.Count];
                SelectedMedText.Text = $"Hovering: {patientName}";
            }
            else if (_mode == Mode.NurseEditMeds)
            {
                // Hovering over medication - store it for gesture editing
                _hoveredMedication = item;
                SelectedMedText.Text = $"Hovering: {item} - Press marker 15 to edit time";
            }
        }

        private void HandleNurseWheelSelect(int sector, string item)
        {
            if (_currentNurseWheelType == NurseWheelType.PatientSelection && _mode == Mode.NurseMode)
            {
                // Patient selected - DIRECTLY show their info
                _currentPatient = PatientNames[sector % PatientNames.Count];
                SelectedMedText.Text = $"Selected: {_currentPatient}";
                Log($"Patient selected: {_currentPatient} - Showing info");
                
                _isNurseWheelActive = false;
                ShowWheel(false);
                
                SetMode(Mode.NurseViewInfo);
                ShowPatientInfoPanel();
            }
            else if (_mode == Mode.NurseEditMeds)
            {
                // Medication selected for editing
                _selectedMedication = item;
                _selectedMedIndex = sector;
                ShowMedicationEditor(item);
            }
        }

        private void SetupWheelForPatientSelection()
        {
            double cx = WheelCanvas.Width / 2;
            double cy = WheelCanvas.Height / 2;
            double radius = 150;
            int n = PatientNames.Count;

            WheelCanvas.Children.Clear();
            _sectorButtons.Clear();

            for (int i = 0; i < n; i++)
            {
                double angle = (2 * Math.PI * i) / n - Math.PI / 2;
                double bx = cx + Math.Cos(angle) * radius;
                double by = cy + Math.Sin(angle) * radius;

                var b = new Button()
                {
                    Width = 120,
                    Height = 40,
                    Content = PatientNames[i],
                    Tag = i,
                    FontSize = 10
                };
                Canvas.SetLeft(b, bx - b.Width / 2);
                Canvas.SetTop(b, by - b.Height / 2);
                b.IsHitTestVisible = false;
                WheelCanvas.Children.Add(b);
                _sectorButtons.Add(b);
            }

            // Add center circle
            var centerEllipse = new Ellipse()
            {
                Width = 80,
                Height = 80,
                Fill = Brushes.LightBlue,
                Stroke = Brushes.Black,
                StrokeThickness = 2
            };
            Canvas.SetLeft(centerEllipse, cx - 40);
            Canvas.SetTop(centerEllipse, cy - 40);
            WheelCanvas.Children.Add(centerEllipse);

            // Add "Patients" text in center
            var centerText = new TextBlock()
            {
                Text = "PATIENTS",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black
            };
            Canvas.SetLeft(centerText, cx - 35);
            Canvas.SetTop(centerText, cy - 8);
            WheelCanvas.Children.Add(centerText);
        }

        private void HandleEditMedicationMode(int sector, string medication)
        {
            // Marker 13 + 15 together detected - enter edit medication mode
            if (_mode == Mode.NurseMode || _mode == Mode.NurseViewInfo)
            {
                SetMode(Mode.NurseEditMeds);
                _isNurseWheelActive = true;
                _currentNurseWheelType = NurseWheelType.MedicationEdit;
                _hoveredMedication = ""; // Clear any previous hover
                SetupWheel(); // Setup medication wheel
                ShowWheel(true);
                SelectedMedText.Text = $"Patient: {_currentPatient} - EDIT MEDICATIONS (Rotate 13, hover on medication, place 15 for gesture)";
            }
        }

        private void HandleGestureModeToggle(bool enabled)
        {
            // Marker 15 alone - toggle gesture mode
            if (_mode == Mode.NurseEditMeds)
            {
                if (enabled)
                {
                    SetMode(Mode.NurseEditingTime);
                    // Initialize time editing using the hovered medication
                    string medicationToEdit = !string.IsNullOrEmpty(_hoveredMedication) ? _hoveredMedication : _selectedMedication;
                    
                    if (!string.IsNullOrEmpty(medicationToEdit))
                    {
                        var schedule = _medicationSchedules.FirstOrDefault(s => s.MedicationName == medicationToEdit);
                        if (schedule != null && schedule.DoseTimes.Any())
                        {
                            StartTimeEditing(medicationToEdit, schedule.DoseTimes.First());
                        }
                        else
                        {
                            StartTimeEditing(medicationToEdit, new TimeSpan(8, 0, 0)); // Default 8:00 AM
                        }
                        SelectedMedText.Text = $"GESTURE MODE: Editing {medicationToEdit} - Move hand left/right";
                    }
                    else
                    {
                        SelectedMedText.Text = "GESTURE MODE: No medication selected. Hover on a medication first.";
                    }
                }
                else
                {
                    SaveEditedTime(); 
                    SelectedMedText.Text = $"Patient: {_currentPatient} - Gesture mode OFF";
                }
            }
        }

        private void HandleGestureSwipe(string direction)
        {
            if (_mode == Mode.NurseEditingTime)
            {
                AdjustMedicationTime(direction);
            }
        }

        private void HandleGestureTimeUpdate(string timeStr, int minutes)
        {
            if (_mode == Mode.NurseEditingTime)
            {
                // Parse the time string (HH:mm format)
                if (TimeSpan.TryParse(timeStr, out TimeSpan newTime))
                {
                    _editingTime = newTime;
                    UpdateTimeDisplay();
                }
            }
        }

        private void HandleGestureTimeFinal(string timeStr, int minutes)
        {
            if (_mode == Mode.NurseEditingTime || !string.IsNullOrEmpty(_editingMedication))
            {
                if (TimeSpan.TryParse(timeStr, out TimeSpan newTime))
                {
                    _editingTime = newTime;
                    UpdateTimeDisplay();
                    SaveEditedTime();
                }
                else
                {
                    Log($"Failed to parse time string: '{timeStr}'");
                }
            }
        }

        private void ShowPatientInfoPanel()
        {
            // Build patient info display            
            var sb = new StringBuilder();
            sb.AppendLine($"{_currentPatient.ToUpper()} - MEDICATION INFO (last 7 days)");
            sb.AppendLine("═══════════════════════════════════════════════");
            sb.AppendLine();
            
            foreach (var schedule in _medicationSchedules)
            {
                if (schedule.Enabled)
                {
                    var lastRecord = _medicationHistory
                        .Where(r => r.MedicationName == schedule.MedicationName && r.Taken)
                        .OrderByDescending(r => r.TimeTaken)
                        .FirstOrDefault();

                    string lastTaken = lastRecord != null ? lastRecord.TimeTaken.ToString("HH:mm") : "Never";
                    
                    sb.AppendLine($"▸ {schedule.MedicationName}");
                    sb.AppendLine($"  • Last taken: {lastTaken}");
                    sb.AppendLine($"  • Scheduled: {string.Join(", ", schedule.DoseTimes.Select(t => t.ToString(@"hh\:mm")))}");
                    sb.AppendLine();
                }
            }

            // Update the panel content and show it
            Dispatcher.Invoke(() =>
            {
                PatientInfoTitle.Text = $"{_currentPatient.ToUpper()} - INFORMATION";
                PatientInfoContent.Text = sb.ToString();
                PatientInfoPanel.Visibility = Visibility.Visible;
                WheelCanvas.Visibility = Visibility.Collapsed; // Hide wheel
            });
        }

        private void HidePatientInfoPanel()
        {
            Dispatcher.Invoke(() =>
            {
                PatientInfoPanel.Visibility = Visibility.Collapsed;
            });
        }

        private void ShowWarningPanel(string message)
        {
            Dispatcher.Invoke(() =>
            {
                // Set warning content
                WarningContent.Text = message;
                WarningPanel.Visibility = Visibility.Visible;
                _warningSecondsRemaining = 10;
                UpdateWarningTimer();
                
                // Create or restart timer
                if (_warningTimer == null)
                {
                    _warningTimer = new System.Windows.Threading.DispatcherTimer();
                    _warningTimer.Interval = TimeSpan.FromSeconds(1);
                    _warningTimer.Tick += WarningTimer_Tick;
                }
                
                _warningTimer.Start();
            });
        }

        private void WarningTimer_Tick(object sender, EventArgs e)
        {
            _warningSecondsRemaining--;
            
            if (_warningSecondsRemaining <= 0)
            {
                HideWarningPanel();
            }
            else
            {
                UpdateWarningTimer();
            }
        }

        private void UpdateWarningTimer()
        {
            Dispatcher.Invoke(() =>
            {
                WarningTimer.Text = $"This warning will close in {_warningSecondsRemaining} second{(_warningSecondsRemaining != 1 ? "s" : "")}";
            });
        }

        private void HideWarningPanel()
        {
            Dispatcher.Invoke(() =>
            {
                _warningTimer?.Stop();
                WarningPanel.Visibility = Visibility.Collapsed;
                Log("Warning panel closed");
            });
        }

        private void ShowSuccessPanel(string message)
        {
            Dispatcher.Invoke(() =>
            {
                // Set success content
                SuccessContent.Text = message;
                SuccessPanel.Visibility = Visibility.Visible;
                _successSecondsRemaining = 5;
                UpdateSuccessTimer();
                
                // Create or restart timer
                if (_successTimer == null)
                {
                    _successTimer = new System.Windows.Threading.DispatcherTimer();
                    _successTimer.Interval = TimeSpan.FromSeconds(1);
                    _successTimer.Tick += SuccessTimer_Tick;
                }
                
                _successTimer.Start();
            });
        }

        private void SuccessTimer_Tick(object sender, EventArgs e)
        {
            _successSecondsRemaining--;
            
            if (_successSecondsRemaining <= 0)
            {
                HideSuccessPanel();
            }
            else
            {
                UpdateSuccessTimer();
            }
        }

        private void UpdateSuccessTimer()
        {
            Dispatcher.Invoke(() =>
            {
                SuccessTimer.Text = $"This message will close in {_successSecondsRemaining} second{(_successSecondsRemaining != 1 ? "s" : "")}";
            });
        }

        private void HideSuccessPanel()
        {
            Dispatcher.Invoke(() =>
            {
                _successTimer?.Stop();
                SuccessPanel.Visibility = Visibility.Collapsed;
                Log("Success panel closed");
            });
        }

        private void ShowMedicationEditor(string medication)
        {
            var schedule = _medicationSchedules.FirstOrDefault(s => s.MedicationName == medication);
            if (schedule == null)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Editing: {medication}");
            sb.AppendLine($"Currently: {(schedule.Enabled ? "ENABLED" : "DISABLED")}");
            sb.AppendLine($"\nScheduled Times:");
            
            if (schedule.DoseTimes.Any())
            {
                for (int i = 0; i < schedule.DoseTimes.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {schedule.DoseTimes[i]:hh\\:mm}");
                }
            }
            else
            {
                sb.AppendLine("  No scheduled times");
            }

            sb.AppendLine("\nOptions:");
            sb.AppendLine("1. Toggle Enable/Disable");
            sb.AppendLine("2. Add New Time");
            sb.AppendLine("3. Edit Existing Time");
            sb.AppendLine("4. Remove Time");

            bool userPressedOK = ShowAlertWithCooldown(
                sb.ToString() + "\n\nPress OK to toggle enable/disable\nPress Cancel to go back",
                "Edit Medication",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (userPressedOK)
            {
                // Toggle enabled state
                schedule.Enabled = !schedule.Enabled;
                _excelManager.SaveMedicationSchedule(_medicationSchedules);
                Log($"{medication} is now {(schedule.Enabled ? "ENABLED" : "DISABLED")}");
                
                // Refresh the schedule display
                PopulateMedicationScheduleDisplay();
                
                // Show updated info
                ShowAlertWithCooldown(
                    $"{medication} is now {(schedule.Enabled ? "ENABLED" : "DISABLED")}",
                    "Medication Updated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private TimeSpan _editingTime = TimeSpan.Zero;
        private string _editingMedication = "";

        private void StartTimeEditing(string medication, TimeSpan initialTime)
        {
            _editingMedication = medication;
            _editingTime = initialTime;
            SetMode(Mode.NurseEditingTime);
            UpdateTimeDisplay();
        }

        private void UpdateTimeDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                SelectedMedText.Text = $"Editing time for {_editingMedication}: {_editingTime:hh\\:mm}";
            });
        }

        private void AdjustMedicationTime(string direction)
        {
            if (_mode != Mode.NurseEditingTime)
                return;

            int adjustment = direction == "left" ? -30 : 30;
            
            // Adjust time by 30 minutes
            _editingTime = _editingTime.Add(TimeSpan.FromMinutes(adjustment));
            
            // Wrap around 24 hours
            if (_editingTime < TimeSpan.Zero)
                _editingTime = _editingTime.Add(TimeSpan.FromHours(24));
            else if (_editingTime >= TimeSpan.FromHours(24))
                _editingTime = _editingTime.Subtract(TimeSpan.FromHours(24));

            UpdateTimeDisplay();
        }

        private void SaveEditedTime()
        {            
            try
            {
                // Only save if we have a medication to edit
                if (string.IsNullOrEmpty(_editingMedication))
                {
                    return;
                }

                var schedule = _medicationSchedules.FirstOrDefault(s => s.MedicationName == _editingMedication);
                if (schedule != null)
                {
                    schedule.DoseTimes.Clear();
                    schedule.DoseTimes.Add(_editingTime);
                    
                    var secondDose = _editingTime.Add(TimeSpan.FromHours(12));
                    if (secondDose.TotalHours >= 24)
                    {
                        secondDose = secondDose.Subtract(TimeSpan.FromHours(24));
                    }
                    schedule.DoseTimes.Add(secondDose);
                    
                    schedule.DoseTimes = schedule.DoseTimes.OrderBy(t => t).ToList();
                    
                    // Save to Excel
                    _excelManager.SaveMedicationSchedule(_medicationSchedules);
                    
                    // Reload schedules from Excel to ensure we're showing the latest data
                    _medicationSchedules = _excelManager.LoadMedicationSchedule();
                    PopulateMedicationScheduleDisplay();
                    
                    Dispatcher.Invoke(() =>
                    {
                        SelectedMedText.Text = $"✓ SAVED: {_editingMedication} → {_editingTime:hh\\:mm} and {secondDose:hh\\:mm}";
                    });
                }
                else
                {
                    Log($"Cannot save: schedule not found for {_editingMedication}");
                }

                SetMode(Mode.NurseEditMeds);
                _editingMedication = "";
                _editingTime = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                Log($"Error in SaveEditedTime: {ex.Message}");
            }
        }

        #endregion

        #region UI Event Handlers
        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear all medication history?",
                "Clear History",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _medicationHistory.Clear();
                UpdateHistoryDisplay();
                Log("Medication history cleared");
            }
        }
        #endregion
    }
}