# Official meaning of the three thresholds (PaddleOCR 3.x docs, English)
Source: https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/pipeline_usage/OCR.en.md
 - text_det_thresh (default 0.3): "If the average score of all pixels within the detected
   result boundary is higher than this threshold, the result will be considered a text region."
   (doc text for thresh says: pixels with scores greater than this threshold are considered text pixels)
 - text_det_box_thresh (default 0.6): "Text detection box threshold. ... If the average score of
   all pixels within the detected result boundary is higher than this threshold, the result will
   be considered a text region."
 - text_det_unclip_ratio (default 1.5): "Expansion ratio for the Vatti clipping algorithm,
   used to expand the text region."
 - text_rec_score_thresh (default 0.0): "Text recognition threshold. Text results with scores
   higher than this threshold will be retained. ... defaults to 0.0, i.e., no threshold"
Source: https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.en.md
 - thresh: "pixels with scores greater than this threshold are considered text pixels"
 - box_thresh: "If the average score of all pixels inside the bounding box is greater than this
   threshold, the result is considered a text region."
 - unclip_ratio: "Expansion ratio for the Vatti clipping algorithm, used to expand the text region."
Implementation: ppocr/postprocess/db_postprocess.py (release/2.7)
  num_contours = min(len(contours), self.max_candidates)
  if sside < self.min_size: continue
  if self.box_thresh > score: continue
  box = self.unclip(points, self.unclip_ratio)...
  if sside < self.min_size + 2: continue
=> so geometry filtering that EXISTS upstream is only min_size (min side length) and max_candidates.
   There is NO aspect-ratio / area / isolation filter in PaddleOCR's postprocess.
