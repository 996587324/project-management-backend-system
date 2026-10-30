using Service;
static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
var day = HourlyShiftRules.At("08:00", "20:00", new DateTime(2026,10,30,8,0,0));
var night = HourlyShiftRules.At("08:00", "20:00", new DateTime(2026,10,30,20,0,0));
Check(day.Code == "Day" && day.End == night.Start, "班次边界无空档");
Check(HourlyShiftRules.Previous("08:00", "20:00", night.Start) == day, "夜班切换到当日白班");
var nextDay = HourlyShiftRules.At("08:00", "20:00", new DateTime(2026,10,31,8,0,0));
Check(HourlyShiftRules.Previous("08:00", "20:00", nextDay.Start) == night, "白班切换到前一夜班");
Check(HourlyShiftRules.At("08:00", "20:00", new DateTime(2026,10,31,1,0,0)) == night, "跨午夜归属夜班");
var shortDay = HourlyShiftRules.At("08:00", "19:30", new DateTime(2026,10,30,12,0,0));
Check(HourlyShiftRules.Slots(shortDay).Last().End == shortDay.End && HourlyShiftRules.Slots(shortDay).Last().Start.Hour == 19, "不足一小时的末段");
try { HourlyShiftRules.Validate("08:00", "08:00"); throw new Exception("重叠时间未拒绝"); } catch (ArgumentException) { Console.WriteLine("PASS 无效边界被拒绝"); }

var board=new Model.HourlyProduction.HourlyBoardDefinition { StartTime="08:00",EndTime="20:00",LegacyTargets=new() {["L/P"]=[50]}, ShiftHistory=[new(){EffectiveAt=new DateTime(2026,10,30,20,0,0),DayStart="08:00",NightStart="20:00",DayTargets=new(){["L/P"]=[100]},NightTargets=new(){["L/P"]=[80]}}]};
Check(HourlyProductionRules.Targets(board,new DateTime(2026,10,30,12,0,0),"L/P",[999])[0]==50,"历史目标冻结");
Check(HourlyProductionRules.Targets(board,new DateTime(2026,10,30,20,0,0),"L/P",[999])[0]==80,"指定边界夜班生效");
Check(HourlyProductionRules.Targets(board,new DateTime(2026,10,31,8,0,0),"L/P",[999])[0]==100,"白班独立目标");
Check(HourlyProductionRules.Window(board,new DateTime(2026,10,31,1,0,0)).Start==new DateTime(2026,10,30,20,0,0),"历史规则选择与夜班归属");
