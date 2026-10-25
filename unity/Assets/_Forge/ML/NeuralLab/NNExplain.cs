using System.Text;

namespace Forge.ML
{
    /// <summary>
    /// The one explanation that fits what the Neural Net Lab shows right now (the "Training story" panel's Why card):
    /// a neuron switched off, an activation or learning rate that can't work, why it's stuck on this dataset with these
    /// inputs, why Albie's new inputs worked, train vs test, or what Play is doing. Most specific first.
    /// </summary>
    public static class NNExplain
    {
        static readonly StringBuilder sb = new StringBuilder(64);

        public static void For(NeuralLabSim sim, out string title, out string body)
        {
            string off = SwitchedOff(sim);
            bool stuck = sim.StalledAt >= 0 && sim.TestAccuracy < 0.9f;
            bool extra = Has(sim, NNFeature.XSquared) || Has(sim, NNFeature.YSquared) || Has(sim, NNFeature.XTimesY)
                         || Has(sim, NNFeature.SinX) || Has(sim, NNFeature.SinY);
            if (off.Length > 0)
            {
                title = "A neuron is switched off";
                body = $"{off} now output{(off.Contains(",") ? "" : "s")} 0, so its picture drops out of the sum the next layer draws. " +
                       "If accuracy fell, it was carrying part of the boundary. The others can learn to cover for it: press Play.";
            }
            else if (sim.Activation == NNActivation.Linear)
            {
                title = "Linear can only draw straight lines";
                body = "Without a bend in the activation, stacking layers adds nothing: a linear function of a linear function is " +
                       "still a straight line. No number of neurons can draw a circle or a spiral this way.";
            }
            else if (sim.LearningRate >= 0.3f)
            {
                title = "The learning rate is too high";
                body = $"At {sim.LearningRate:0.###} every step jumps past the bottom of the error, so the weights bounce around " +
                       "instead of settling. Smaller steps (0.03 or less) take longer but land.";
            }
            else if (stuck && sim.Activation == NNActivation.Sigmoid) Sigmoid(out title, out body, true);
            else if (stuck && !extra) Stuck(sim, out title, out body);
            else if (stuck)
            {
                title = $"Stuck at {sim.TestAccuracy:0%}";
                body = "The accuracy stopped climbing: the network has learned all it can with this many neurons and these " +
                       "inputs. More neurons, better inputs or a different activation would let it bend further. Ask Albie.";
            }
            else if (sim.Noise >= 0.2f)
            {
                title = "Noise sets a ceiling";
                body = $"{sim.Noise:0%} noise flips the colors of points near the edge. No network can get those right, so the best " +
                       "test accuracy is below 100%, and a network that gets every training point right is memorizing noise.";
            }
            else if (!sim.Playing && sim.Epoch > 0 && sim.TestAccuracy >= 0.9f && extra)
            {
                title = "Why the inputs made the difference";
                body = Features(sim) + " Choosing inputs that match the pattern (feature engineering) often beats a bigger network.";
            }
            else if (sim.Epoch > 300 && !sim.Playing && sim.TrainAccuracy - sim.TestAccuracy > 0.08f)
            {
                title = "Overfitting";
                body = $"Train {sim.TrainAccuracy:0%} but test {sim.TestAccuracy:0%}: it's learning the training points themselves, " +
                       "not the pattern, so it does worse on points it hasn't seen. Fewer neurons or less noise helps.";
            }
            else if (!sim.Playing && sim.Epoch > 0 && sim.TestAccuracy >= 0.9f)
            {
                title = "Train vs test";
                body = $"Train accuracy ({sim.TrainAccuracy:0%}) is measured on the 250 points it learns from, test ({sim.TestAccuracy:0%}) " +
                       "on 250 it has never seen. Both high and close together means it learned the pattern, not the points.";
            }
            else if (sim.Playing)
            {
                title = "What Play is doing";
                body = "Each epoch, all 250 training points go through the network, 10 at a time. After every batch each weight " +
                       "moves a little in the direction that lowers the error (gradient descent). Thicker lines are bigger weights.";
            }
            else if (sim.Epoch == 0)
            {
                title = "Start here";
                body = "The network has to color the plane so blue points land on blue and orange on orange. Its weights start " +
                       "random, so the picture is noise. Press Play: the left screen follows one point through it, the right " +
                       "one shows what each neuron learns.";
            }
            else if (sim.Activation == NNActivation.Sigmoid) Sigmoid(out title, out body, false);
            else if (sim.Activation == NNActivation.ReLU)
            {
                title = "ReLU";
                body = "ReLU outputs 0 for anything negative and passes the rest. It's fast and the default in big networks, but a " +
                       "neuron that's below 0 for every point stops learning for good (a dead neuron), and the boundary comes out " +
                       "in straight-edged pieces.";
            }
            else Stuck(sim, out title, out body);
        }

        static void Sigmoid(out string title, out string body, bool stuck)
        {
            title = stuck ? "Sigmoid learns slowly" : "Sigmoid";
            body = "Sigmoid outputs 0 to 1, never negative, and its slope is at most 0.25, so the error signal shrinks at every " +
                   "layer it passes back through and learning crawls. tanh is the same S-shape centered on 0, with 4× the slope.";
        }

        // Why this dataset needs more than the inputs it has (also the default card for each dataset).
        static void Stuck(NeuralLabSim sim, out string title, out string body)
        {
            string inputs = sim.FeatureList();
            switch (sim.Dataset)
            {
                case NNDataset.Spiral:
                    title = "Why the spiral is hard";
                    body = $"The two arms wind around the center. On {inputs} alone, each first-layer neuron draws one straight line, " +
                           "and a handful of lines can't wrap around a spiral. sin x and sin y repeat in waves that follow the arms, " +
                           "x² and y² give curves, and more neurons give it more pieces to combine.";
                    break;
                case NNDataset.Circle:
                    title = "Why the circle needs x² and y²";
                    body = "Blue sits inside a ring of orange. A neuron on x and y draws one straight line, so the network has to " +
                           "build the circle out of several. x² + y² is the distance from the center: give it x² and y² and a " +
                           "single neuron can draw the circle.";
                    break;
                case NNDataset.Xor:
                    title = "Why XOR needs x·y";
                    body = "Blue where x and y have the same sign, orange where they differ: no single straight line splits that. " +
                           "The sign of x·y is exactly the answer, so with x·y as an input it's one cut; without it the network " +
                           "needs several lines in just the right places.";
                    break;
                default:
                    title = "Two blobs, one line";
                    body = "One blob per color: a single straight line separates them, so x and y are all it needs and it learns " +
                           "almost at once. If it's stuck here, the activation or the learning rate is the problem, not the inputs.";
                    break;
            }
        }

        static string Features(NeuralLabSim sim)
        {
            sb.Clear();
            if (Has(sim, NNFeature.XSquared) || Has(sim, NNFeature.YSquared))
                sb.Append("x² and y² let a neuron measure the distance from the center, so it can draw circles. ");
            if (Has(sim, NNFeature.SinX) || Has(sim, NNFeature.SinY))
                sb.Append("sin x and sin y rise and fall in waves, which follow a spiral's arms. ");
            if (Has(sim, NNFeature.XTimesY))
                sb.Append("x·y is positive in two opposite corners and negative in the other two: XOR in one input. ");
            return sb.ToString().TrimEnd();
        }

        static bool Has(NeuralLabSim sim, NNFeature f) => sim.Features[(int)f];

        static string SwitchedOff(NeuralLabSim sim)
        {
            sb.Clear();
            for (int l = 1; l <= sim.Hidden.Count; l++)
                for (int i = 0; i < sim.Hidden[l - 1]; i++)
                    if (sim.Net.disabled[l][i]) { if (sb.Length > 0) sb.Append(", "); sb.Append(NeuralLabSim.NeuronId(l, i + 1)); }
            return sb.ToString();
        }
    }
}
