using System.Text.Json;
using Model.SystemApplications;
using Model.WorkTasks;

namespace Service;

public sealed partial class WorkTaskService
{
    public async Task HandoverAsync(WorkActor actor,WorkHandoverRequest request)
    {
        Ready();Scope(actor,"admin");if(actor.Personal)throw new UnauthorizedAccessException("个人凭证不能交接管理责任");
        using var db=scope.GetConnection("calmadmin").CopyNew();
        var tx=await db.Ado.UseTranAsync(async()=>
        {
            await LockAsync(db,"work-task:"+request.TaskId);
            var task=await db.Queryable<WorkTask>().FirstAsync(x=>x.Id==request.TaskId&&x.AppCode==actor.AppCode)
                ??throw new UnauthorizedAccessException("任务不存在或无权办理交接");
            var responsible=(task.DispatcherId==actor.UserId||task.SummaryLeaderId==actor.UserId)&&await Has(db,actor,["dispatcher"],task.GroupId,task.AssigneeId);
            if(!responsible)await Admin(db,actor,task.GroupId,task.AssigneeId);
            State(task,"PendingAssessment","InProgress","PendingReview");
            if(task.Version!=request.ExpectedVersion)throw new WorkConflictException("任务已变化，请刷新后重新交接");
            var reason=Text(request.Reason,2000,"交接原因",true);
            var before=new{task.DispatcherId,task.ReviewerId,task.SummaryLeaderId};
            task.DispatcherId=Text(request.DispatcherId,64,"新的下派负责人",true);
            task.SummaryLeaderId=string.IsNullOrWhiteSpace(request.SummaryLeaderId)?task.DispatcherId:Text(request.SummaryLeaderId,64,"汇总负责人",true);
            task.ReviewerId=string.IsNullOrWhiteSpace(request.ReviewerId)?task.DispatcherId:Text(request.ReviewerId,64,"验收人",true);
            foreach(var (user,review) in new[]{(task.DispatcherId,true),(task.SummaryLeaderId,false),(task.ReviewerId,true)})
            {
                if(user==task.ReviewerId&&!task.RequiresReview&&user!=task.DispatcherId&&user!=task.SummaryLeaderId)continue;
                await ValidatePerson(db,actor.AppCode,user);
                var manager=new WorkActor(new ApplicationAccessContext{AppCode=actor.AppCode,UserId=user});
                if(!await Has(db,manager,review?["dispatcher"]:["dispatcher","supervisor"],task.GroupId,task.AssigneeId))throw new ArgumentException("新负责人缺少本任务范围的对应授权");
            }
            if(task.RequiresReview&&task.ReviewerId==task.AssigneeId)throw new ArgumentException("不能交接成自验收任务");
            task.Version++;task.UpdatedAtUtc=DateTime.UtcNow;
            await db.Updateable(task).ExecuteCommandAsync();
            await Audit(db,actor,task.Id,"management.handover",JsonSerializer.Serialize(new{Before=before,After=new{task.DispatcherId,task.ReviewerId,task.SummaryLeaderId},Reason=reason},Json));
            await db.Insertable(new WorkEvent{AppCode=actor.AppCode,TaskId=task.Id,Action="management.handover",SnapshotJson=JsonSerializer.Serialize(task,Json)}).ExecuteCommandAsync();
        });
        if(!tx.IsSuccess)throw tx.ErrorException!;
    }
}
