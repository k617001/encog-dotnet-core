//
// Encog(tm) Core v3.0 - .Net Version
// http://www.heatonresearch.com/encog/
//
// Copyright 2008-2011 Heaton Research, Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//   
// For more information on Heaton Research copyrights, licenses 
// and trademarks visit:
// http://www.heatonresearch.com/copyright
//
using System;
using Encog.Engine.Network.Activation;
using Encog.MathUtil.Error;
using Encog.ML.Data;
using Encog.ML.Data.Basic;
using Encog.Neural.Flat.Train.Prop;
using Encog.Util;
using Encog.Util.Concurrency;

namespace Encog.Neural.Flat.Train.Prop
{
    /// <summary>
    /// Worker class for the mulithreaded training of flat networks.
    /// </summary>
    ///
    public class GradientWorker: IEngineTask
    {
        /// <summary>
        /// The actual values from the neural network.
        /// </summary>
        ///
        private readonly double[] actual;

        /// <summary>
        /// The error calculation method.
        /// </summary>
        ///
        private readonly ErrorCalculation errorCalculation;

        /// <summary>
        /// The gradients.
        /// </summary>
        ///
        private readonly double[] gradients;

        /// <summary>
        /// The low end of the training.
        /// </summary>
        ///
        private readonly int high;

        /// <summary>
        /// The neuron counts, per layer.
        /// </summary>
        ///
        private readonly int[] layerCounts;

        /// <summary>
        /// The deltas for each layer.
        /// </summary>
        ///
        private readonly double[] layerDelta;

        /// <summary>
        /// The feed counts, per layer.
        /// </summary>
        ///
        private readonly int[] layerFeedCounts;

        /// <summary>
        /// The layer indexes.
        /// </summary>
        ///
        private readonly int[] layerIndex;

        /// <summary>
        /// The output from each layer.
        /// </summary>
        ///
        private readonly double[] layerOutput;

        /// <summary>
        /// The high end of the training data.
        /// </summary>
        ///
        private readonly int low;

        /// <summary>
        /// The network to train.
        /// </summary>
        ///
        private readonly FlatNetwork network;

        /// <summary>
        /// The owner.
        /// </summary>
        ///
        private readonly TrainFlatNetworkProp owner;

        /// <summary>
        /// The pair to use for training.
        /// </summary>
        ///
        private readonly IMLDataPair pair;

        /// <summary>
        /// The training data.
        /// </summary>
        ///
        private readonly IMLDataSet training;

        /// <summary>
        /// The index to each layer's weights and thresholds.
        /// </summary>
        ///
        private readonly int[] weightIndex;

        /// <summary>
        /// The weights and thresholds.
        /// </summary>
        ///
        private readonly double[] weights;

        /// <summary>
        /// Derivative add constant.  Used to combat flat spot.
        /// </summary>
        private double[] _flatSpot;


        /// <summary>
        /// Construct a gradient worker.
        /// </summary>
        ///
        /// <param name="theNetwork">The network to train.</param>
        /// <param name="theOwner">The owner that is doing the training.</param>
        /// <param name="theTraining">The training data.</param>
        /// <param name="theLow">The low index to use in the training data.</param>
        /// <param name="theHigh">The high index to use in the training data.</param>
        /// <param name="theFlatSpots">Holds an array of flat spot constants.</param>
        public GradientWorker(FlatNetwork theNetwork,
                                 TrainFlatNetworkProp theOwner, IMLDataSet theTraining,
                                 int theLow, int theHigh, double[] theFlatSpots)
        {
            errorCalculation = new ErrorCalculation();
            network = theNetwork;
            training = theTraining;
            low = theLow;
            high = theHigh;
            owner = theOwner;
            _flatSpot = theFlatSpots;

            layerDelta = new double[network.LayerOutput.Length];
            gradients = new double[network.Weights.Length];
            actual = new double[network.OutputCount];

            weights = network.Weights;
            layerIndex = network.LayerIndex;
            layerCounts = network.LayerCounts;
            weightIndex = network.WeightIndex;
            layerOutput = network.LayerOutput;
            layerFeedCounts = network.LayerFeedCounts;

            pair = BasicMLDataPair.CreatePair(network.InputCount,
                                              network.OutputCount);
        }

        #region FlatGradientWorker Members

        /// <inheritdoc/>
        public FlatNetwork Network
        {
            get { return network; }
        }


        /// <value>The weights for this network.</value>
        public double[] Weights
        {
            get { return weights; }
        }

        /// <summary>
        /// Perform the gradient calculation for the specified index range.
        /// </summary>
        ///
        public void Run()
        {
            try
            {
                errorCalculation.Reset();
                for (int i = low; i <= high; i++)
                {
                    training.GetRecord(i, pair);
                    Process(pair.InputArray, pair.IdealArray);
                }
                double error = errorCalculation.Calculate();
                owner.Report(gradients, error, null);
                EngineArray.Fill(gradients, 0);
            }
            catch (Exception ex)
            {
                owner.Report(null, 0, ex);
            }
        }

        #endregion

        /// <summary>
        /// Process one training set element.
        /// </summary>
        ///
        /// <param name="input">The network input.</param>
        /// <param name="ideal">The ideal values.</param>
        private void Process(double[] input, double[] ideal)
        {
            network.Compute(input, actual);

            errorCalculation.UpdateError(actual, ideal);

            for (int i = 0; i < actual.Length; i++)
            {
                layerDelta[i] = (network.ActivationFunctions[0]
                                    .DerivativeFunction(actual[i])+_flatSpot[0])
                                *(ideal[i] - actual[i]);
            }

            for (int i = network.BeginTraining; i < network.EndTraining; i++)
            {
                ProcessLevel(i);
            }
        }

        /// <summary>
        /// Process one level.
        /// </summary>
        ///
        /// <param name="currentLevel">The level.</param>
        private void ProcessLevel(int currentLevel)
        {
            int fromLayerIndex = layerIndex[currentLevel + 1];
            int toLayerIndex = layerIndex[currentLevel];
            int fromLayerSize = layerCounts[currentLevel + 1];
            int toLayerSize = layerFeedCounts[currentLevel];

            int index = weightIndex[currentLevel];
            IActivationFunction activation = network.ActivationFunctions[currentLevel + 1];
            double currentFlatSpot = _flatSpot[currentLevel + 1];

            // handle weights
            int yi = fromLayerIndex;
            for (int y = 0; y < fromLayerSize; y++)
            {
                double output = layerOutput[yi];
                double sum = 0;
                int xi = toLayerIndex;
                int wi = index + y;
                for (int x = 0; x < toLayerSize; x++)
                {
                    gradients[wi] += output*layerDelta[xi];
                    sum += weights[wi]*layerDelta[xi];
                    wi += fromLayerSize;
                    xi++;
                }

                layerDelta[yi] = sum
                                 *(activation.DerivativeFunction(layerOutput[yi])+currentFlatSpot);
                yi++;
            }
        }
    }
}