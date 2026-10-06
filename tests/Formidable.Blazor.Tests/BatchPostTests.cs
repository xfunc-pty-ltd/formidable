using Formidable.Blazor.Tests.Fixtures;

namespace Formidable.Blazor.Tests;

/// <summary>
/// <see cref="BatchPost"/> posts its callback once however many requests arrive before that post
/// runs, and a callback that throws leaves the next request free to post again.
/// </summary>
public class BatchPostTests
{
    // Two requests in one call stack make one post and one callback. Mutation: drop BatchPost's
    // pending flag, and PostCount is 2 with two callbacks.
    [Fact]
    public void A_request_while_one_is_pending_posts_nothing()
    {
        var context = new QueueingSynchronizationContext();
        var runs = 0;
        var post = new BatchPost(() => runs++);

        using (context.Install())
        {
            post.Request();
            post.Request();
        }

        Assert.Equal(1, post.PostCount);
        Assert.Equal(1, context.Pending);
        Assert.Equal(0, runs);

        context.Drain();

        Assert.Equal(1, runs);
    }

    // The first callback throws, and a later request still posts and runs. Mutation: clear the
    // pending flag after the callback instead of before, and the second request posts nothing.
    [Fact]
    public void A_callback_that_throws_leaves_the_next_request_free_to_post()
    {
        var context = new QueueingSynchronizationContext();
        var runs = 0;
        var post = new BatchPost(() =>
        {
            runs++;
            if (runs == 1)
            {
                throw new InvalidOperationException("the first callback throws");
            }
        });

        using (context.Install())
        {
            post.Request();
        }

        Assert.Throws<InvalidOperationException>(context.Drain);
        Assert.Equal(1, runs);

        using (context.Install())
        {
            post.Request();
        }

        Assert.Equal(2, post.PostCount);
        context.Drain();
        Assert.Equal(2, runs);
    }
}
