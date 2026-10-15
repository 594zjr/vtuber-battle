// T-47 验收测试：教学关卡规则层。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T47_TutorialTests
{
    [Fact]
    public void FourLessonsCompleteWithScriptedAnswers()
    {
        var db = BattleTests.LoadPack("original");
        var lessons = Tutorial.Lessons(db);
        Assert.True(lessons.Count >= 4);
        foreach (var lesson in lessons)
        {
            var run = new TutorialRunner(db, lesson);
            int guard = 0;
            while (!run.Done && guard++ < 100)
            {
                Assert.False(string.IsNullOrEmpty(run.CurrentHint));
                var r = run.Submit(run.Expected);
                Assert.True(r.Accepted, r.Message);
            }
            Assert.True(run.Done, lesson.Title + " 没有完成");
        }
    }

    [Fact]
    public void WrongActionIsRejectedWithHint()
    {
        var db = BattleTests.LoadPack("original");
        var run = new TutorialRunner(db, Tutorial.Lessons(db)[0]);
        var wrong = run.Expected.Type == ActionType.Pass ? BattleAction.Skill(0) : BattleAction.Pass();
        int turn = run.Battle.Turn;
        var r = run.Submit(wrong);
        Assert.False(r.Accepted);
        Assert.False(string.IsNullOrEmpty(r.Message));
        Assert.Equal(turn, run.Battle.Turn); // 没有结算
    }

    [Fact]
    public void TutorialBattlesAreDeterministic()
    {
        var db = BattleTests.LoadPack("original");
        string Run()
        {
            var run = new TutorialRunner(db, Tutorial.Lessons(db)[1]);
            while (!run.Done) run.Submit(run.Expected);
            return string.Join("\n", run.Battle.Log.Select(l => l.Text));
        }
        Assert.Equal(Run(), Run());
    }
}
