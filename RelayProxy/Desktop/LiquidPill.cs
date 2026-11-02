using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace RelayProxy.Desktop;

// The navigation pill as a blob of water. Its top and bottom edges are two separate springs: the
// edge that leads the move arrives first and overshoots, the trailing edge lags behind, so the drop
// stretches while it travels, then squashes and wobbles as it settles. The width gives back what
// the height takes, so the volume of the drop stays about constant.
public sealed class LiquidPill
{
    private const double Rest = 45, Step = 1d / 240;
    private readonly FrameworkElement pill;
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform move = new();
    private double top, topSpeed, bottom, bottomSpeed, topTarget, bottomTarget, goal, direction;
    private bool landed = true;
    private double topStiffness = 240, bottomStiffness = 240, topDamping = .5, bottomDamping = .5;
    private double sway, swaySpeed;
    private bool running, trailingIsTop;
    private long last;

    public LiquidPill(FrameworkElement pill)
    {
        this.pill = pill;
        // Continue from wherever the plain indicator currently sits.
        double start = pill.RenderTransform is TransformGroup { Children.Count: 2 } group && group.Children[1] is TranslateTransform t ? t.Y : 0;
        top = topTarget = goal = start; bottom = bottomTarget = start + Rest;
        pill.RenderTransformOrigin = new Point(.5, .5);
        pill.RenderTransform = new TransformGroup { Children = { scale, move } };
        Apply();
    }

    public void Flow(double offset)
    {
        if (!GlassMotion.CanAnimate) { Snap(offset); return; }
        double direction = Math.Sign(offset - goal);
        if (direction == 0) return;
        goal = offset; this.direction = direction; landed = false;
        // The leading edge is quick and bouncy, the trailing edge soft and late.
        (double lead, double leadZeta, double trail, double trailZeta) = (460, .4, 230, .6);
        trailingIsTop = direction > 0;
        if (direction > 0) { bottomStiffness = lead; bottomDamping = leadZeta; topStiffness = trail; topDamping = trailZeta; }
        else { topStiffness = lead; topDamping = leadZeta; bottomStiffness = trail; bottomDamping = trailZeta; }
        swaySpeed += direction * 6;
        Scene.Hold(1200);
        Start();
    }

    public void Snap(double offset)
    {
        top = topTarget = goal = offset; bottom = bottomTarget = offset + Rest; topSpeed = bottomSpeed = sway = swaySpeed = 0;
        Stop(); Apply();
    }

    // Hands the pill back to the plain indicator.
    public void Release()
    {
        Stop();
        pill.RenderTransform = Transform.Identity;
    }
    public double Offset => goal;
    // Raised once per move, the moment the leading edge reaches its slot: the drop touches down.
    public event Action<double>? Landed;

    private void Start()
    {
        if (running) return;
        running = true; last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Tick;
    }
    private void Stop() { if (!running) return; running = false; CompositionTarget.Rendering -= Tick; }

    private void Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        double dt = Math.Min(.04, (now - last) / (double)Stopwatch.Frequency); last = now;
        // The goal is approached through a short low-pass, so the drop eases into motion instead of
        // being yanked: acceleration builds up smoothly.
        double follow = 1 - Math.Exp(-Step / .045);
        for (double spent = 0; spent < dt; spent += Step)
        {
            topTarget += (goal - topTarget) * follow; bottomTarget += (goal + Rest - bottomTarget) * follow;
            Spring(ref top, ref topSpeed, topTarget, topStiffness, topDamping);
            Spring(ref bottom, ref bottomSpeed, bottomTarget, bottomStiffness, bottomDamping);
            Spring(ref sway, ref swaySpeed, 0, 300, .22);
            // The edges never cross: a drop cannot turn inside out.
            if (bottom - top < 24) { double mid = (top + bottom) / 2; top = mid - 12; bottom = mid + 12; }
            // The drop can only stretch so far before its tail catches up.
            else if (bottom - top > Rest + 56) { if (trailingIsTop) top = bottom - (Rest + 56); else bottom = top + (Rest + 56); }
        }
        Apply();
        if (!landed && (direction > 0 ? bottom >= goal + Rest - 1 : top <= goal + 1)) { landed = true; Landed?.Invoke(goal); }
        if (Math.Abs(top - goal) + Math.Abs(bottom - goal - Rest) + Math.Abs(topTarget - goal) * 2 + Math.Abs(bottom - bottomTarget) + Math.Abs(topSpeed) * .02 + Math.Abs(bottomSpeed) * .02 + Math.Abs(sway) + Math.Abs(swaySpeed) * .02 < .05)
        {
            top = topTarget = goal; bottom = bottomTarget = goal + Rest; topSpeed = bottomSpeed = sway = swaySpeed = 0;
            Apply(); Stop();
        }
    }

    private static void Spring(ref double position, ref double speed, double target, double stiffness, double damping)
    {
        double acceleration = -stiffness * (position - target) - 2 * damping * Math.Sqrt(stiffness) * speed;
        speed += acceleration * Step; position += speed * Step;
    }

    private void Apply()
    {
        double height = bottom - top, stretch = height / Rest;
        scale.ScaleY = stretch;
        scale.ScaleX = Math.Clamp(1 - .45 * (stretch - 1), .8, 1.07) + sway * .015;
        move.Y = (top + bottom) / 2 - Rest / 2;
    }
}
