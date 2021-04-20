using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace HotChocolate.Execution.Processing
{
    public class TrackableTaskSchedulerTests
    {
        [Fact]
        public void Basic()
        {
            var scheduler = new TrackableTaskScheduler(TaskScheduler.Current);
            Assert.True(scheduler.IsIdle);
            Assert.True(scheduler.WaitTillIdle().IsCompleted);
        }
    }
}
