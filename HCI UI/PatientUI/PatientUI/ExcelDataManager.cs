using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TuioPatientUI
{
    public class ExcelDataManager
    {
        private const string EXCEL_FILE = "patient_medications.xlsx";
        private const string SCHEDULE_SHEET = "MedicationSchedule";
        private const string HISTORY_SHEET = "History";

        public class MedicationSchedule
        {
            public string MedicationName { get; set; }
            public bool Enabled { get; set; }
            public List<TimeSpan> DoseTimes { get; set; } = new List<TimeSpan>();
        }

        public class MedicationHistoryRecord
        {
            public string MedicationName { get; set; }
            public DateTime TimeTaken { get; set; }
            public TimeSpan ScheduledTime { get; set; }
            public bool Taken { get; set; }
            public DateTime NextDoseTime { get; set; }
        }

        public void InitializeExcelFile()
        {
            if (File.Exists(EXCEL_FILE))
                return;

            using var workbook = new XLWorkbook();

            // Create MedicationSchedule sheet
            var scheduleSheet = workbook.Worksheets.Add(SCHEDULE_SHEET);
            scheduleSheet.Cell(1, 1).Value = "MedicationName";
            scheduleSheet.Cell(1, 2).Value = "Enabled";
            scheduleSheet.Cell(1, 3).Value = "DoseTimes";
            
            // Add default medications
            var defaultMedications = new List<string>
            {
                "Paracetamol", "Amoxicillin", "Aspirin", "Metformin",
                "Lisinopril", "Atorvastatin"
            };

            for (int i = 0; i < defaultMedications.Count; i++)
            {
                scheduleSheet.Cell(i + 2, 1).Value = defaultMedications[i];
                scheduleSheet.Cell(i + 2, 2).Value = true; // All enabled by default
                scheduleSheet.Cell(i + 2, 3).Value = "04:30,16:30"; // Default: 4:30 AM and 4:30 PM
            }

            // Create History sheet
            var historySheet = workbook.Worksheets.Add(HISTORY_SHEET);
            historySheet.Cell(1, 1).Value = "MedicationName";
            historySheet.Cell(1, 2).Value = "TimeTaken";
            historySheet.Cell(1, 3).Value = "ScheduledTime";
            historySheet.Cell(1, 4).Value = "Taken";
            historySheet.Cell(1, 5).Value = "NextDoseTime";

            workbook.SaveAs(EXCEL_FILE);
        }

        public List<MedicationSchedule> LoadMedicationSchedule()
        {
            try
            {
                InitializeExcelFile();
                var schedules = new List<MedicationSchedule>();

                using var workbook = new XLWorkbook(EXCEL_FILE);
                var worksheet = workbook.Worksheet(SCHEDULE_SHEET);

                var rows = worksheet.RowsUsed().Skip(1); // Skip header

                foreach (var row in rows)
                {
                    var schedule = new MedicationSchedule
                    {
                        MedicationName = row.Cell(1).GetString(),
                        Enabled = row.Cell(2).GetBoolean(),
                        DoseTimes = new List<TimeSpan>()
                    };

                    var doseTimesStr = row.Cell(3).GetString();
                    if (!string.IsNullOrEmpty(doseTimesStr))
                    {
                        var times = doseTimesStr.Split(',');
                        foreach (var time in times)
                        {
                            if (TimeSpan.TryParse(time.Trim(), out TimeSpan timeSpan))
                            {
                                schedule.DoseTimes.Add(timeSpan);
                            }
                        }
                    }
                    schedules.Add(schedule);
                }
                return schedules;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading: {ex.Message}");
                throw;
            }
        }

        public void SaveMedicationSchedule(List<MedicationSchedule> schedules)
        {
            try
            {
                InitializeExcelFile();

                using var workbook = new XLWorkbook(EXCEL_FILE);
                var worksheet = workbook.Worksheet(SCHEDULE_SHEET);

                // Clear existing data (except header)
                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
                if (lastRow > 1)
                {
                    worksheet.Range(2, 1, lastRow, 3).Clear();
                }

                // Write schedules
                for (int i = 0; i < schedules.Count; i++)
                {
                    var schedule = schedules[i];
                    worksheet.Cell(i + 2, 1).Value = schedule.MedicationName;
                    worksheet.Cell(i + 2, 2).Value = schedule.Enabled;
                    
                    var doseTimesStr = string.Join(",", schedule.DoseTimes.Select(t => t.ToString(@"hh\:mm")));
                    worksheet.Cell(i + 2, 3).Value = doseTimesStr;
                }

                workbook.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Excel] ✗ ERROR saving: {ex.Message}");
                throw;
            }
        }

        public List<MedicationHistoryRecord> LoadHistory()
        {
            InitializeExcelFile();
            var history = new List<MedicationHistoryRecord>();

            using var workbook = new XLWorkbook(EXCEL_FILE);
            var worksheet = workbook.Worksheet(HISTORY_SHEET);

            var rows = worksheet.RowsUsed().Skip(1); // Skip header

            foreach (var row in rows)
            {
                var record = new MedicationHistoryRecord
                {
                    MedicationName = row.Cell(1).GetString(),
                    TimeTaken = row.Cell(2).GetDateTime(),
                    ScheduledTime = TimeSpan.Parse(row.Cell(3).GetString()),
                    Taken = row.Cell(4).GetBoolean(),
                    NextDoseTime = row.Cell(5).GetDateTime()
                };

                history.Add(record);
            }

            return history;
        }

        public void AppendHistory(MedicationHistoryRecord record)
        {
            InitializeExcelFile();

            using var workbook = new XLWorkbook(EXCEL_FILE);
            var worksheet = workbook.Worksheet(HISTORY_SHEET);

            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            var newRow = lastRow + 1;

            worksheet.Cell(newRow, 1).Value = record.MedicationName;
            worksheet.Cell(newRow, 2).Value = record.TimeTaken;
            worksheet.Cell(newRow, 3).Value = record.ScheduledTime.ToString(@"hh\:mm");
            worksheet.Cell(newRow, 4).Value = record.Taken;
            worksheet.Cell(newRow, 5).Value = record.NextDoseTime;

            workbook.Save();
        }
    }
}

