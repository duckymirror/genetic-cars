-- Does crossover with 40%, mutate with 3 bit flips

require 'DoCrossOver'
require 'FlipGenomeBits'

function CrossOver(genomeA, genomeB)
  return DoCrossOver(genomeA, genomeB, 0.4)
end

function Mutate(genome)
  return FlipGenomeBits(genome, 3)
end